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
    private const int MaxSseTail = 200_000;

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

        builder.Services.AddSingleton(new RunStore(config.StateDirectory, config.PersistRawEvents));
        builder.Services.AddSingleton<RunEventBroadcaster>();
        builder.Services.AddSingleton<RunRollupWriter>();

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
                    name: "codex-d-http",
                    title: "CodexD (HTTP)",
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

        app.MapGet("/v1/info", (HttpRequest req, ServerConfig cfg) =>
        {
            var version = typeof(Host).Assembly.GetName().Version?.ToString();
            var host = req.Host.ToUriComponent();
            var baseUrl = string.IsNullOrWhiteSpace(host) ? cfg.BaseUrl : $"{req.Scheme}://{host}";
            var port = req.Host.Port ?? cfg.Port;
            return Results.Ok(new
            {
                startedAtUtc = cfg.StartedAtUtc,
                runnerId = cfg.Identity.RunnerId,
                version,
                listen = cfg.ListenAddress.ToString(),
                port,
                requireAuth = cfg.RequireAuth,
                stateDir = cfg.StateDirectory,
                baseUrl
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

            try
            {
                var created = await runs.CreateAndStartAsync(request with { Kind = kind }, ct);
                return Results.Ok(new { runId = created.RunId, status = created.Status });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_request", message = ex.Message });
            }
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
                try
                {
                    cwd = PathPolicy.TrimTrailingSeparators(Path.GetFullPath(cwd));
                }
                catch
                {
                    return Results.BadRequest(new { error = "invalid_cwd" });
                }
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

        app.MapPost("/v1/runs/{runId:guid}/stop", async (Guid runId, RunManager runs, CancellationToken ct) =>
        {
            var ok = await runs.TryStopAsync(runId, ct);
            return ok ? Results.Accepted() : Results.NotFound(new { error = "not_found_or_not_running" });
        });

        app.MapPost("/v1/runs/{runId:guid}/resume", async (Guid runId, ResumeRunRequest? request, RunManager runs, CancellationToken ct) =>
        {
            var prompt = string.IsNullOrWhiteSpace(request?.Prompt) ? "continue" : request!.Prompt!.Trim();

            var resumed = await runs.ResumeAsync(runId, prompt, ct);
            return resumed is null
                ? Results.NotFound(new { error = "not_found_or_not_resumable" })
                : Results.Ok(new { runId = resumed.RunId, status = resumed.Status });
        });

        app.MapGet("/v1/runs/{runId:guid}/messages", async (Guid runId, HttpRequest req, RunStore store, CancellationToken ct) =>
        {
            var record = await store.TryGetAsync(runId, ct);
            if (record is null)
            {
                return Results.NotFound(new { error = "not_found" });
            }

            var count = ParseInt(req.Query["count"]) ?? 1;
            if (count <= 0)
            {
                return Results.BadRequest(new { error = "count_must_be_positive" });
            }

            count = Math.Min(count, 50);

            var tailEvents = ParseInt(req.Query["tailEvents"]) ?? 20000;
            if (tailEvents <= 0)
            {
                return Results.BadRequest(new { error = "tail_events_must_be_positive" });
            }

            tailEvents = Math.Min(tailEvents, 200000);

            var queue = new Queue<object>(Math.Min(count, 50));

            var dir = await store.TryResolveRunDirectoryAsync(runId, ct);
            if (dir is not null && HasRawEventsFile(dir))
            {
                var events = await store.ReadRawEventsAsync(runId, tailEvents, ct);
                foreach (var env in events)
                {
                    if (!string.Equals(env.Type, "codex.notification", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!RunEventDataExtractors.TryGetCompletedAgentMessageText(env.Data, out var text) ||
                        string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (queue.Count == count)
                    {
                        queue.Dequeue();
                    }

                    queue.Enqueue(new { createdAt = env.CreatedAt, text });
                }
            }
            else
            {
                var rollup = await store.ReadRollupAsync(runId, tailEvents, ct);
                foreach (var rec in rollup)
                {
                    if (!string.Equals(rec.Type, "agentMessage", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var text = rec.Text;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    if (queue.Count == count)
                    {
                        queue.Dequeue();
                    }

                    queue.Enqueue(new { createdAt = rec.CreatedAt, text });
                }
            }

            return Results.Ok(new { items = queue.ToArray() });
        });

        app.MapGet("/v1/runs/{runId:guid}/thinking-summaries", async (Guid runId, HttpRequest req, RunStore store, CancellationToken ct) =>
        {
            var record = await store.TryGetAsync(runId, ct);
            if (record is null)
            {
                return Results.NotFound(new { error = "not_found" });
            }

            var tailEvents = ParseInt(req.Query["tailEvents"]) ?? 20000;
            if (tailEvents <= 0)
            {
                return Results.BadRequest(new { error = "tail_events_must_be_positive" });
            }

            tailEvents = Math.Min(tailEvents, 200000);

            var summaries = new List<string>();
            var last = string.Empty;
            var inThinking = false;

            var dir = await store.TryResolveRunDirectoryAsync(runId, ct);
            if (dir is not null && HasRawEventsFile(dir))
            {
                var events = await store.ReadRawEventsAsync(runId, tailEvents, ct);
                foreach (var env in events)
                {
                    if (!string.Equals(env.Type, "codex.notification", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!RunEventDataExtractors.TryGetOutputDelta(env.Data, out var delta) ||
                        string.IsNullOrWhiteSpace(delta))
                    {
                        continue;
                    }

                    var trimmed = delta.Trim();
                    if (string.Equals(trimmed, "thinking", StringComparison.OrdinalIgnoreCase))
                    {
                        inThinking = true;
                        continue;
                    }

                    if (string.Equals(trimmed, "final", StringComparison.OrdinalIgnoreCase))
                    {
                        inThinking = false;
                        continue;
                    }

                    var maybeThinking = inThinking || delta.Contains("thinking", StringComparison.OrdinalIgnoreCase);
                    if (!maybeThinking)
                    {
                        continue;
                    }

                    foreach (var rawLine in delta.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var t = rawLine.Trim();
                        if (!t.StartsWith("**", StringComparison.Ordinal) || !t.EndsWith("**", StringComparison.Ordinal) || t.Length <= 4)
                        {
                            continue;
                        }

                        var summary = t[2..^2].Trim();
                        if (string.IsNullOrWhiteSpace(summary))
                        {
                            continue;
                        }

                        if (string.Equals(summary, last, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        summaries.Add(summary);
                        last = summary;
                    }
                }
            }
            else
            {
                var rollup = await store.ReadRollupAsync(runId, tailEvents, ct);
                foreach (var rec in rollup)
                {
                    if (!string.Equals(rec.Type, "outputLine", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var text = rec.Text ?? string.Empty;
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    if (rec.IsControl == true)
                    {
                        var trimmed = text.Trim();
                        if (string.Equals(trimmed, "thinking", StringComparison.OrdinalIgnoreCase))
                        {
                            inThinking = true;
                        }
                        else if (string.Equals(trimmed, "final", StringComparison.OrdinalIgnoreCase))
                        {
                            inThinking = false;
                        }
                        continue;
                    }

                    if (!inThinking)
                    {
                        continue;
                    }

                    var t = text.Trim();
                    if (!t.StartsWith("**", StringComparison.Ordinal) || !t.EndsWith("**", StringComparison.Ordinal) || t.Length <= 4)
                    {
                        continue;
                    }

                    var summary = t[2..^2].Trim();
                    if (string.IsNullOrWhiteSpace(summary))
                    {
                        continue;
                    }

                    if (string.Equals(summary, last, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    summaries.Add(summary);
                    last = summary;
                }
            }

            return Results.Ok(new { items = summaries });
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

            var replayRaw = ctx.Request.Query["replay"].ToString();
            var followRaw = ctx.Request.Query["follow"].ToString();
            var tailRaw = ctx.Request.Query["tail"].ToString();

            var replay = true;
            if (!string.IsNullOrWhiteSpace(replayRaw) && !bool.TryParse(replayRaw, out replay))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid_replay" }, ct);
                return;
            }

            var follow = true;
            if (!string.IsNullOrWhiteSpace(followRaw) && !bool.TryParse(followRaw, out follow))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid_follow" }, ct);
                return;
            }

            int? tail = null;
            if (!string.IsNullOrWhiteSpace(tailRaw))
            {
                if (!int.TryParse(tailRaw, out var t))
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_tail" }, ct);
                    return;
                }

                if (t <= 0)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(new { error = "tail_must_be_positive" }, ct);
                    return;
                }

                tail = Math.Min(t, MaxSseTail);
            }

            var replayFormat = (ctx.Request.Query["replayFormat"].ToString() ?? "auto").Trim();

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
                var replaySawTerminal = false;

                var dir = await store.TryResolveRunDirectoryAsync(runId, ct);
                var hasRaw = dir is not null && HasRawEventsFile(dir);
                var useRawReplay = replayFormat.Length == 0
                    ? hasRaw
                    : replayFormat.Trim().ToLowerInvariant() switch
                    {
                        "auto" => hasRaw,
                        "raw" => true,
                        "rollup" => false,
                        _ => hasRaw
                    };

                if (replay)
                {
                    if (useRawReplay)
                    {
                        if (tail is { } tailValue)
                        {
                            var events = await store.ReadRawEventsAsync(runId, tailValue, ct);
                            foreach (var env in events)
                            {
                                if (string.Equals(env.Type, "run.meta", StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                maxReplayedAt = maxReplayedAt is null || env.CreatedAt > maxReplayedAt.Value ? env.CreatedAt : maxReplayedAt;
                                if (string.Equals(env.Type, "run.completed", StringComparison.Ordinal) ||
                                    string.Equals(env.Type, "run.paused", StringComparison.Ordinal))
                                {
                                    replaySawTerminal = true;
                                }

                                await SseWriter.WriteEventAsync(
                                    ctx.Response,
                                    env.Type,
                                    env.Data.GetRawText(),
                                    ct);
                            }
                        }
                        else
                        {
                            await foreach (var env in store.EnumerateRawEventsAsync(runId, ct))
                            {
                                if (string.Equals(env.Type, "run.meta", StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                maxReplayedAt = maxReplayedAt is null || env.CreatedAt > maxReplayedAt.Value ? env.CreatedAt : maxReplayedAt;
                                if (string.Equals(env.Type, "run.completed", StringComparison.Ordinal) ||
                                    string.Equals(env.Type, "run.paused", StringComparison.Ordinal))
                                {
                                    replaySawTerminal = true;
                                }

                                await SseWriter.WriteEventAsync(
                                    ctx.Response,
                                    env.Type,
                                    env.Data.GetRawText(),
                                    ct);
                            }
                        }
                    }
                    else
                    {
                        if (tail is { } tailValue)
                        {
                            var rollup = await store.ReadRollupAsync(runId, tailValue, ct);
                            foreach (var rec in rollup)
                            {
                                var eventName = rec.Type switch
                                {
                                    "outputLine" => "codex.rollup.outputLine",
                                    "agentMessage" => "codex.rollup.agentMessage",
                                    _ => null
                                };

                                if (eventName is null)
                                {
                                    continue;
                                }

                                await SseWriter.WriteEventAsync(
                                    ctx.Response,
                                    eventName,
                                    JsonSerializer.Serialize(rec, Json),
                                    ct);
                            }
                        }
                        else
                        {
                            await foreach (var rec in store.EnumerateRollupAsync(runId, ct))
                            {
                                var eventName = rec.Type switch
                                {
                                    "outputLine" => "codex.rollup.outputLine",
                                    "agentMessage" => "codex.rollup.agentMessage",
                                    _ => null
                                };

                                if (eventName is null)
                                {
                                    continue;
                                }

                                await SseWriter.WriteEventAsync(
                                    ctx.Response,
                                    eventName,
                                    JsonSerializer.Serialize(rec, Json),
                                    ct);
                            }
                        }
                    }
                }

                var latest = await store.TryGetAsync(runId, ct);
                if (!replaySawTerminal && latest is not null && IsTerminal(latest.Status))
                {
                    // Ensure the client sees a terminal event even when we only replay rollups.
                    var terminalEventName = string.Equals(latest.Status, RunStatuses.Paused, StringComparison.Ordinal)
                        ? "run.paused"
                        : "run.completed";
                    await SseWriter.WriteEventAsync(
                        ctx.Response,
                        terminalEventName,
                        JsonSerializer.Serialize(latest, Json),
                        ct);
                    replaySawTerminal = true;
                }

                if (!follow || replaySawTerminal)
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

                        if (useRawReplay && maxReplayedAt is not null && env.CreatedAt < maxReplayedAt.Value)
                        {
                            continue;
                        }

                        await SseWriter.WriteEventAsync(
                            ctx.Response,
                            env.Type,
                            env.Data.GetRawText(),
                            ct);

                        if (string.Equals(env.Type, "run.completed", StringComparison.Ordinal) ||
                            string.Equals(env.Type, "run.paused", StringComparison.Ordinal))
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

    private static bool HasRawEventsFile(string runDirectory) =>
        File.Exists(Path.Combine(runDirectory, "events.jsonl"));

    private static bool IsTerminal(string status) =>
        string.Equals(status, RunStatuses.Succeeded, StringComparison.Ordinal) ||
        string.Equals(status, RunStatuses.Failed, StringComparison.Ordinal) ||
        string.Equals(status, RunStatuses.Paused, StringComparison.Ordinal) ||
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
