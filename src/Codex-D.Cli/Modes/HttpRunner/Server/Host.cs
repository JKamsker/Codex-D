using System.Net;
using System.Text.Json;
using CodexD.HttpRunner.CodexRuntime;
using CodexD.HttpRunner.Contracts;
using CodexD.Shared.Paths;
using CodexD.HttpRunner.Runs;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.ApprovalHandlers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexD.HttpRunner.Server;

public static class Host
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebApplication Build(
        ServerConfig config,
        Action<IServiceCollection>? configureServices = null,
        bool enableCodexRuntime = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>()
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(x =>
        {
            x.SingleLine = true;
            x.TimestampFormat = "HH:mm:ss ";
        });

        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.WriteIndented = false;
        });

        builder.Services.AddSingleton(config);

        builder.Services.AddSingleton(new RunStore(config.StateDirectory));
        builder.Services.AddSingleton<RunEventBroadcaster>();

        builder.Services.AddSingleton<RuntimeState>();
        if (enableCodexRuntime)
        {
            builder.Services.AddSingleton<IAppServerClientProvider>(sp => sp.GetRequiredService<RuntimeState>());
            builder.Services.AddSingleton<CodexAppServerRunExecutor>();
            builder.Services.AddSingleton<CodexReviewRunExecutor>();
            builder.Services.AddSingleton<IRunExecutor, DispatchingRunExecutor>();
        }
        builder.Services.AddSingleton<RunManager>();

        builder.Services.AddOptions<ProcessHostOptions>();

        if (enableCodexRuntime)
        {
            builder.Services.AddCodexAppServerClient(options =>
            {
                options.DefaultClientInfo = new AppServerClientInfo(
                    name: "codex-runner-http",
                    title: "Codex Runner (HTTP)",
                    version: typeof(Host).Assembly.GetName().Version?.ToString() ?? "0.0.0");

                options.ApprovalHandler = new AlwaysDenyHandler();

                // Keep parity with the API host’s override mechanism (optional).
                var sandboxPermissions = Environment.GetEnvironmentVariable("CODEXWEBUI_CODEX_SANDBOX_PERMISSIONS")?.Trim();
                if (string.IsNullOrWhiteSpace(sandboxPermissions))
                {
                    sandboxPermissions = "[\"disk-full-read-access\"]";
                }

                var shellEnvInherit = Environment.GetEnvironmentVariable("CODEXWEBUI_CODEX_SHELL_ENV_INHERIT")?.Trim();
                if (string.IsNullOrWhiteSpace(shellEnvInherit))
                {
                    shellEnvInherit = "all";
                }

                var overrides = new List<string>
                {
                    $"sandbox_permissions={sandboxPermissions}",
                    $"shell_environment_policy.inherit={shellEnvInherit}"
                };

                var extraOverrides = Environment.GetEnvironmentVariable("CODEXWEBUI_CODEX_CONFIG_OVERRIDES");
                if (!string.IsNullOrWhiteSpace(extraOverrides))
                {
                    overrides.AddRange(extraOverrides.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }

                foreach (var configOverride in overrides)
                {
                    options.Launch = options.Launch.WithArgs("-c", configOverride);
                }
            });

            builder.Services.AddHostedService<ProcessHost>();
        }

        configureServices?.Invoke(builder.Services);

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(config.ListenAddress, config.Port);
        });

        var app = builder.Build();

        if (config.RequireAuth)
        {
            app.Use(async (ctx, next) =>
            {
                if (!Auth.IsAuthorized(ctx.Request, config.Identity.Token))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }, ctx.RequestAborted);
                    return;
                }

                await next();
            });
        }

        app.MapGet("/v1/health", (RuntimeState state) =>
        {
            if (!enableCodexRuntime)
            {
                return Results.Ok(new
                {
                    status = "ok",
                    codexRuntime = "disabled"
                });
            }

            var ready = state.TryGetClient() is not null;
            return Results.Ok(new
            {
                status = "ok",
                codexRuntime = ready ? "ready" : "starting"
            });
        });

        app.MapGet("/v1/info", (ServerConfig cfg) =>
        {
            var version = typeof(Host).Assembly.GetName().Version?.ToString();
            return Results.Ok(new
            {
                startedAtUtc = cfg.StartedAtUtc,
                runnerId = cfg.Identity.RunnerId,
                version,
                listen = cfg.ListenAddress.ToString(),
                port = cfg.Port,
                requireAuth = cfg.RequireAuth,
                stateDir = cfg.StateDirectory,
                baseUrl = cfg.BaseUrl
            });
        });

        app.MapPost("/v1/runs", async (CreateRunRequest request, RunManager runs, CancellationToken ct) =>
        {
            if (request is null)
            {
                return Results.BadRequest(new { error = "missing_body" });
            }

            if (string.IsNullOrWhiteSpace(request.Cwd))
            {
                return Results.BadRequest(new { error = "cwd_required" });
            }

            var inferredKind = string.IsNullOrWhiteSpace(request.Kind)
                ? (request.Review is not null ? RunKinds.Review : null)
                : request.Kind;

            var kind = RunKinds.Normalize(inferredKind);
            if (kind is not (RunKinds.Exec or RunKinds.Review))
            {
                return Results.BadRequest(new { error = "invalid_kind", kind });
            }

            if (kind == RunKinds.Exec)
            {
                if (string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return Results.BadRequest(new { error = "prompt_required" });
                }
            }
            else if (kind == RunKinds.Review)
            {
                if (request.Review is null)
                {
                    return Results.BadRequest(new { error = "review_required" });
                }

                var review = request.Review;
                if (!review.Uncommitted &&
                    string.IsNullOrWhiteSpace(review.CommitSha) &&
                    string.IsNullOrWhiteSpace(review.BaseBranch))
                {
                    review = review with { Uncommitted = true };
                }

                request = request with { Kind = kind, Review = review, Prompt = request.Prompt ?? string.Empty };
            }

            var created = await runs.CreateAndStartAsync(request with { Kind = kind }, ct);
            return Results.Ok(new { runId = created.RunId, status = created.Status });
        });

        app.MapGet("/v1/runs", async (HttpRequest req, RunStore store, CancellationToken ct) =>
        {
            var all = ParseBool(req.Query["all"]) ?? false;
            var cwd = req.Query["cwd"].ToString();

            if (!all && string.IsNullOrWhiteSpace(cwd))
            {
                return Results.BadRequest(new { error = "cwd_required_unless_all" });
            }

            if (!all)
            {
                cwd = PathPolicy.TrimTrailingSeparators(Path.GetFullPath(cwd));
            }

            var items = await store.ListAsync(all ? null : cwd, all, ct);
            return Results.Ok(new { items });
        });

        app.MapGet("/v1/runs/{runId:guid}", async (Guid runId, RunStore store, CancellationToken ct) =>
        {
            var record = await store.TryGetAsync(runId, ct);
            return record is null ? Results.NotFound(new { error = "not_found" }) : Results.Ok(record);
        });

        app.MapPost("/v1/runs/{runId:guid}/interrupt", async (Guid runId, RunManager runs, CancellationToken ct) =>
        {
            var ok = await runs.TryInterruptAsync(runId, ct);
            return ok ? Results.Accepted() : Results.NotFound(new { error = "not_found_or_not_running" });
        });

        app.MapGet("/v1/runs/{runId:guid}/events", async (Guid runId, HttpContext ctx, RunStore store, RunEventBroadcaster broadcaster) =>
        {
            var ct = ctx.RequestAborted;
            var record = await store.TryGetAsync(runId, ct);
            if (record is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = "not_found" }, ct);
                return;
            }

            var replay = ParseBool(ctx.Request.Query["replay"]) ?? true;
            var follow = ParseBool(ctx.Request.Query["follow"]) ?? true;
            var tail = ParseInt(ctx.Request.Query["tail"]);

            SseWriter.ConfigureHeaders(ctx.Response);

            RunEventSubscription? sub = null;
            try
            {
                // Subscribe early when following so we don't miss events published while we are replaying history.
                // We de-dup replayed events by CreatedAt below.
                if (follow && !IsTerminal(record.Status))
                {
                    sub = broadcaster.Subscribe(runId);
                }

                await SseWriter.WriteEventAsync(
                    ctx.Response,
                    "run.meta",
                    JsonSerializer.Serialize(record, Json),
                    ct);

                DateTimeOffset? maxReplayedAt = null;
                var replaySawCompleted = false;

                if (replay)
                {
                    var events = await store.ReadEventsAsync(runId, tail, ct);
                    foreach (var env in events)
                    {
                        if (string.Equals(env.Type, "run.meta", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        maxReplayedAt = maxReplayedAt is null || env.CreatedAt > maxReplayedAt.Value ? env.CreatedAt : maxReplayedAt;
                        if (string.Equals(env.Type, "run.completed", StringComparison.Ordinal))
                        {
                            replaySawCompleted = true;
                        }

                        await SseWriter.WriteEventAsync(
                            ctx.Response,
                            env.Type,
                            env.Data.GetRawText(),
                            ct);
                    }
                }

                if (!follow || replaySawCompleted)
                {
                    return;
                }

                var latest = await store.TryGetAsync(runId, ct);
                if (latest is not null && IsTerminal(latest.Status))
                {
                    return;
                }

                if (sub is null)
                {
                    return;
                }

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                var pingTask = Task.Run(async () =>
                {
                    while (await pingTimer.WaitForNextTickAsync(pingCts.Token))
                    {
                        await SseWriter.WriteCommentAsync(ctx.Response, "ping", pingCts.Token);
                    }
                }, CancellationToken.None);

                try
                {
                    await foreach (var env in sub.Reader.ReadAllAsync(ct))
                    {
                        if (string.Equals(env.Type, "run.meta", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (maxReplayedAt is not null && env.CreatedAt <= maxReplayedAt.Value)
                        {
                            continue;
                        }

                        await SseWriter.WriteEventAsync(
                            ctx.Response,
                            env.Type,
                            env.Data.GetRawText(),
                            ct);

                        if (string.Equals(env.Type, "run.completed", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // client disconnected
                }
                finally
                {
                    try { pingCts.Cancel(); } catch { }
                    pingTimer.Dispose();
                    try { await pingTask; } catch { }
                }
            }
            finally
            {
                if (sub is not null)
                {
                    try
                    {
                        await sub.DisposeAsync();
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        });

        return app;
    }

    private static bool IsTerminal(string status) =>
        string.Equals(status, RunStatuses.Succeeded, StringComparison.Ordinal) ||
        string.Equals(status, RunStatuses.Failed, StringComparison.Ordinal) ||
        string.Equals(status, RunStatuses.Interrupted, StringComparison.Ordinal);

    private static bool? ParseBool(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : bool.TryParse(value, out var b)
                ? b
                : null;

    private static int? ParseInt(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : int.TryParse(value, out var i)
                ? i
                : null;

    private static bool? ParseBool(Microsoft.Extensions.Primitives.StringValues value) =>
        ParseBool(value.ToString());

    private static int? ParseInt(Microsoft.Extensions.Primitives.StringValues value) =>
        ParseInt(value.ToString());
}
