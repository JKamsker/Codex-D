using System.Net;
using System.Text;
using System.Text.Json;
using CodexD.HttpRunner.CodexRuntime;
using CodexD.HttpRunner.Contracts;
using CodexD.Shared.Paths;
using CodexD.HttpRunner.Runs;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.ApprovalHandlers;
using JKToolKit.CodexSDK.AppServer.Protocol.V2;
using JKToolKit.CodexSDK.AppServer.Resiliency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

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
        if (config.JsonLogs)
        {
            builder.Logging.AddJsonConsole();
        }
        else
        {
            builder.Logging.AddSimpleConsole(x =>
            {
                x.SingleLine = true;
                x.TimestampFormat = "HH:mm:ss ";
            });
        }

        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.WriteIndented = false;
        });

        builder.Services.AddSingleton(config);

        builder.Services.AddSingleton(new RunStore(config.StateDirectory, config.PersistRawEvents));
        builder.Services.AddSingleton<RunEventBroadcaster>();
        builder.Services.AddSingleton<RunNotificationBacklog>();
        builder.Services.AddSingleton<CodexRolloutRollupReader>();

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

                var experimentalApi = Environment.GetEnvironmentVariable("CODEX_D_EXPERIMENTAL_API")?.Trim();
                if (!string.IsNullOrWhiteSpace(experimentalApi) &&
                    bool.TryParse(experimentalApi, out var enableExperimental) &&
                    enableExperimental)
                {
                    options.ExperimentalApi = true;
                }

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

            var client = state.TryGetClient();
            var codexRuntime = client is null
                ? "starting"
                : client.State switch
                {
                    CodexAppServerConnectionState.Connected => "ready",
                    CodexAppServerConnectionState.Restarting => "restarting",
                    CodexAppServerConnectionState.Faulted => "faulted",
                    CodexAppServerConnectionState.Disposed => "disposed",
                    _ => "unknown"
                };
            return Results.Ok(new
            {
                status = "ok",
                codexRuntime
            });
        });

        app.MapGet("/v1/info", (HttpRequest req, ServerConfig cfg) =>
        {
            var asm = typeof(Host).Assembly;
            var version = asm.GetName().Version?.ToString();
            var informationalVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
            var host = req.Host.ToUriComponent();
            var baseUrl = string.IsNullOrWhiteSpace(host) ? cfg.BaseUrl : $"{req.Scheme}://{host}";
            var port = req.Host.Port ?? cfg.Port;
            return Results.Ok(new
            {
                startedAtUtc = cfg.StartedAtUtc,
                runnerId = cfg.Identity.RunnerId,
                version,
                informationalVersion,
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
                var prompt = request.Prompt ?? string.Empty;

                static string NormalizeReviewMode(string? raw)
                {
                    raw = raw?.Trim();
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        return "exec";
                    }

                    return raw.Equals("appserver", StringComparison.OrdinalIgnoreCase) ||
                           raw.Equals("app-server", StringComparison.OrdinalIgnoreCase)
                        ? "appserver"
                        : "exec";
                }

                var normalizedMode = NormalizeReviewMode(review.Mode);
                var hasPrompt = !string.IsNullOrWhiteSpace(prompt);
                var hasTarget =
                    review.Uncommitted ||
                    !string.IsNullOrWhiteSpace(review.CommitSha) ||
                    !string.IsNullOrWhiteSpace(review.BaseBranch);
                var sandbox = string.IsNullOrWhiteSpace(request.Sandbox) ? null : request.Sandbox.Trim();

                // Default scope:
                // - exec-mode: when a prompt is provided without scope, upstream codex treats it as the target (custom instructions).
                // - appserver-mode: prompt is developer instructions, so default to uncommitted changes when no explicit scope is provided.
                if (!hasTarget && (!hasPrompt || normalizedMode == "appserver"))
                {
                    review = review with { Uncommitted = true };
                    hasTarget = true;
                }

                // Upstream `codex review` cannot combine scope selection with a prompt; treat prompt as developer instructions instead.
                if (normalizedMode == "exec" && hasPrompt && hasTarget)
                {
                    if (review.AdditionalOptions is { Length: > 0 })
                    {
                        throw new ArgumentException(
                            "In exec-mode, `codex review` cannot combine --uncommitted/--base/--commit with a custom PROMPT. " +
                            "Remove exec-only extra args (AdditionalOptions) or switch this review to appserver-mode.");
                    }

                    review = review with { Mode = "appserver" };
                    normalizedMode = "appserver";
                }

                // Default sandbox for app-server reviews to read-only (unless explicitly overridden).
                if (sandbox is null && normalizedMode == "appserver")
                {
                    sandbox = "read-only";
                }

                request = request with { Kind = kind, Review = review, Prompt = prompt, Sandbox = sandbox };
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

        app.MapGet("/v1/runs", async (HttpRequest req, RunStore store, RunNotificationBacklog backlog, CancellationToken ct) =>
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
            var merged = new List<Run>(items.Count);
            foreach (var item in items)
            {
                merged.Add(WithCodexLastNotificationAt(item, backlog));
            }
            return Results.Ok(new { items = merged });
        });

        app.MapGet("/v1/runs/{runId:guid}", async (Guid runId, RunStore store, RunNotificationBacklog backlog, CancellationToken ct) =>
        {
            var record = await store.TryGetAsync(runId, ct);
            return record is null ? Results.NotFound(new { error = "not_found" }) : Results.Ok(WithCodexLastNotificationAt(record, backlog));
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

            var resumed = await runs.ResumeAsync(runId, prompt, request?.Effort, ct);
            return resumed is null
                ? Results.NotFound(new { error = "not_found_or_not_resumable" })
                : Results.Ok(new { runId = resumed.RunId, status = resumed.Status });
        });

        if (enableCodexRuntime)
        {
            app.MapPost("/v1/runs/{runId:guid}/steer", async (
                Guid runId,
                SteerRunRequest? request,
                RunStore store,
                CodexD.HttpRunner.CodexRuntime.IAppServerClientProvider codex,
                CancellationToken ct) =>
            {
                if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return Results.BadRequest(new { error = "prompt_required" });
                }

                var record = await store.TryGetAsync(runId, ct);
                if (record is null)
                {
                    return Results.NotFound(new { error = "not_found" });
                }

                if (string.IsNullOrWhiteSpace(record.CodexThreadId) || string.IsNullOrWhiteSpace(record.CodexTurnId))
                {
                    return Results.Conflict(new { error = "run_missing_codex_ids" });
                }

                var client = await codex.GetClientAsync(ct);

                try
                {
                    await client.CallAsync(
                        "turn/steer",
                        new TurnSteerParams
                        {
                            ThreadId = record.CodexThreadId,
                            ExpectedTurnId = record.CodexTurnId,
                            Input = new object[] { TurnInputItem.Text(request.Prompt.Trim()).Wire }
                        },
                        ct);

                    return Results.Ok(new { status = "ok" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = "steer_failed", message = ex.Message });
                }
            });
        }

        app.MapGet("/v1/runs/{runId:guid}/messages", async (Guid runId, HttpRequest req, RunStore store, RunNotificationBacklog backlog, CancellationToken ct) =>
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

            var dir = await store.TryResolveRunDirectoryAsync(runId, ct);
            var hasRawEvents = dir is not null && HasRawEventsFile(dir);
            if (hasRawEvents)
            {
                var queue = new Queue<object>(Math.Min(count, 50));
                var events = await store.ReadRawEventsAsync(runId, tailEvents, ct);
                var maxNotificationAt = events.Where(e => string.Equals(e.Type, "codex.notification", StringComparison.Ordinal))
                    .Select(e => (DateTimeOffset?)e.CreatedAt)
                    .Max();

                foreach (var (createdAt, text) in ExtractCompletedMessages(events))
                {
                    EnqueueCompletedMessage(queue, count, createdAt, text);
                }

                var snapshot = backlog.SnapshotAfter(runId, maxNotificationAt);
                if (snapshot.Count > tailEvents)
                {
                    snapshot = snapshot.Skip(snapshot.Count - tailEvents).ToArray();
                }

                foreach (var (createdAt, text) in ExtractCompletedMessages(snapshot))
                {
                    EnqueueCompletedMessage(queue, count, createdAt, text);
                }

                return Results.Ok(new { items = queue.ToArray() });
            }

            var history = new List<(DateTimeOffset CreatedAt, string Text)>(count);

            var rolloutPath = CodexRolloutPathNormalizer.Normalize(record.CodexRolloutPath);
            if (!string.IsNullOrWhiteSpace(rolloutPath) && File.Exists(rolloutPath))
            {
                var items = await CodexRolloutMessagesReader.ReadAssistantMessagesAsync(rolloutPath, count, ct);
                foreach (var item in items)
                {
                    history.Add((item.CreatedAt, item.Text));
                }
            }

            var snapshotPending = backlog.SnapshotPending(runId);
            if (snapshotPending.Count > tailEvents)
            {
                snapshotPending = snapshotPending.Skip(snapshotPending.Count - tailEvents).ToArray();
            }

            var pending = new List<(DateTimeOffset CreatedAt, string Text)>();
            foreach (var (createdAt, text) in ExtractCompletedMessages(snapshotPending))
            {
                pending.Add((createdAt, text));
            }

            var overlap = FindMessageOverlap(history, pending);

            var combined = new List<(DateTimeOffset CreatedAt, string Text)>(history.Count + pending.Count - overlap);
            combined.AddRange(history);
            for (var i = overlap; i < pending.Count; i++)
            {
                combined.Add(pending[i]);
            }

            if (combined.Count > count)
            {
                combined = combined.Skip(combined.Count - count).ToList();
            }

            return Results.Ok(new { items = combined.Select(x => new { createdAt = x.CreatedAt, text = x.Text }).ToArray() });
        });

        app.MapGet("/v1/runs/{runId:guid}/thinking-summaries", async (Guid runId, HttpRequest req, RunStore store, RunNotificationBacklog backlog, CancellationToken ct) =>
        {
            var record = await store.TryGetAsync(runId, ct);
            if (record is null)
            {
                return Results.NotFound(new { error = "not_found" });
            }

            var timestamps = ParseBool(req.Query["timestamps"]) ?? false;

            var tailEvents = ParseInt(req.Query["tailEvents"]) ?? 20000;
            if (tailEvents <= 0)
            {
                return Results.BadRequest(new { error = "tail_events_must_be_positive" });
            }

            tailEvents = Math.Min(tailEvents, 200000);

            var dir = await store.TryResolveRunDirectoryAsync(runId, ct);
            var hasRawEvents = dir is not null && HasRawEventsFile(dir);
            if (hasRawEvents)
            {
                var raw = await store.ReadRawEventsAsync(runId, tailEvents, ct);
                var maxNotificationAt = raw.Where(e => string.Equals(e.Type, "codex.notification", StringComparison.Ordinal))
                    .Select(e => (DateTimeOffset?)e.CreatedAt)
                    .Max();

                var pending = backlog.SnapshotAfter(runId, maxNotificationAt);
                if (pending.Count > tailEvents)
                {
                    pending = pending.Skip(pending.Count - tailEvents).ToArray();
                }

                var combined = raw.Concat(pending).ToArray();
                if (timestamps)
                {
                    var items = ThinkingSummaries.FromRawEventsWithTimestamps(combined);
                    return Results.Ok(new { items = items.Select(x => new { createdAt = x.CreatedAt, text = x.Text }).ToArray() });
                }

                return Results.Ok(new { items = ThinkingSummaries.FromRawEvents(combined) });
            }

            var rolloutPath = CodexRolloutPathNormalizer.Normalize(record.CodexRolloutPath);
            if (!string.IsNullOrWhiteSpace(rolloutPath) && File.Exists(rolloutPath))
            {
                var pending = backlog.SnapshotPending(runId);
                if (pending.Count > tailEvents)
                {
                    pending = pending.Skip(pending.Count - tailEvents).ToArray();
                }

                if (timestamps)
                {
                    var items = ThinkingSummaries.FromCodexRolloutThenRawEventsWithTimestamps(rolloutPath, pending, ct);
                    return Results.Ok(new { items = items.Select(x => new { createdAt = x.CreatedAt, text = x.Text }).ToArray() });
                }

                return Results.Ok(new { items = ThinkingSummaries.FromCodexRolloutThenRawEvents(rolloutPath, pending, ct) });
            }

            var snapshot = backlog.SnapshotAfter(runId, afterExclusive: null);
            if (snapshot.Count > tailEvents)
            {
                snapshot = snapshot.Skip(snapshot.Count - tailEvents).ToArray();
            }

            if (timestamps)
            {
                var items = ThinkingSummaries.FromRawEventsWithTimestamps(snapshot);
                return Results.Ok(new { items = items.Select(x => new { createdAt = x.CreatedAt, text = x.Text }).ToArray() });
            }

            return Results.Ok(new { items = ThinkingSummaries.FromRawEvents(snapshot) });
        });

        app.MapGet("/v1/runs/{runId:guid}/events", async (Guid runId, HttpContext ctx, RunStore store, RunEventBroadcaster broadcaster, RunNotificationBacklog backlog, CodexRolloutRollupReader rolloutReader) =>
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
                string? lastReplayedType = null;

                var dir = await store.TryResolveRunDirectoryAsync(runId, ct);
                var hasRaw = dir is not null && HasRawEventsFile(dir);
                var rolloutPath = CodexRolloutPathNormalizer.Normalize(record.CodexRolloutPath);
                var hasRollout = !string.IsNullOrWhiteSpace(rolloutPath) && File.Exists(rolloutPath);

                var mode = replayFormat.Length == 0 ? "auto" : replayFormat.Trim().ToLowerInvariant();
                var useRawReplay = mode == "raw" || (mode == "auto" && !hasRollout && hasRaw);
                var useRollupReplay = mode == "rollup" || (mode == "auto" && hasRollout);

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
                                lastReplayedType = env.Type;

                                await SseWriter.WriteEventAsync(
                                    ctx.Response,
                                    env.Type,
                                    WithCreatedAt(env.CreatedAt, env.Data),
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
                                lastReplayedType = env.Type;

                                await SseWriter.WriteEventAsync(
                                    ctx.Response,
                                    env.Type,
                                    WithCreatedAt(env.CreatedAt, env.Data),
                                    ct);
                            }
                        }
                    }
                    else if (useRollupReplay && rolloutPath is not null)
                    {
                        var replayed = await rolloutReader.ReadAsync(rolloutPath, tail, ct);
                        var maxRolloutAt = replayed.MaxCreatedAt;
                        lastReplayedType = "codex.rollup";

                        foreach (var rec in replayed.Records)
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

                        if (follow)
                        {
                            DateTimeOffset? maxGapAt = null;
                            var gap = backlog.SnapshotAfter(runId, maxRolloutAt);
                            foreach (var env in gap)
                            {
                                await SseWriter.WriteEventAsync(ctx.Response, env.Type, WithCreatedAt(env.CreatedAt, env.Data), ct);
                                maxGapAt = maxGapAt is null || env.CreatedAt > maxGapAt.Value ? env.CreatedAt : maxGapAt;
                            }

                            maxReplayedAt = maxGapAt;
                        }
                    }
                }

                var latest = await store.TryGetAsync(runId, ct);
                if (latest is not null && IsTerminal(latest.Status))
                {
                    // Ensure the client sees a terminal event even when we only replay rollups.
                    var terminalEventName = string.Equals(latest.Status, RunStatuses.Paused, StringComparison.Ordinal)
                        ? "run.paused"
                        : "run.completed";

                    if (!useRawReplay || !string.Equals(lastReplayedType, terminalEventName, StringComparison.Ordinal))
                    {
                        await SseWriter.WriteEventAsync(
                            ctx.Response,
                            terminalEventName,
                            JsonSerializer.Serialize(latest, Json),
                            ct);
                    }

                    return;
                }

                if (!follow)
                {
                    return;
                }

                if (sub is null)
                {
                    sub = broadcaster.Subscribe(runId);
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

                        if (maxReplayedAt is not null && env.CreatedAt < maxReplayedAt.Value)
                        {
                            continue;
                        }

                        await SseWriter.WriteEventAsync(
                            ctx.Response,
                            env.Type,
                            WithCreatedAt(env.CreatedAt, env.Data),
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

    private static string WithCreatedAt(DateTimeOffset createdAt, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return data.GetRawText();
        }

        if (data.TryGetProperty("createdAt", out _))
        {
            return data.GetRawText();
        }

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("createdAt", createdAt);

        foreach (var prop in data.EnumerateObject())
        {
            writer.WritePropertyName(prop.Name);
            prop.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static int FindMessageOverlap(
        IReadOnlyList<(DateTimeOffset CreatedAt, string Text)> history,
        IReadOnlyList<(DateTimeOffset CreatedAt, string Text)> pending)
    {
        if (history.Count == 0 || pending.Count == 0)
        {
            return 0;
        }

        var createdAtTolerance = TimeSpan.FromSeconds(5);
        var maxOverlap = Math.Min(history.Count, pending.Count);
        for (var overlap = maxOverlap; overlap >= 1; overlap--)
        {
            var historyStart = history.Count - overlap;
            var match = true;
            for (var i = 0; i < overlap; i++)
            {
                var h = history[historyStart + i];
                var p = pending[i];
                if (!string.Equals(h.Text, p.Text, StringComparison.Ordinal) ||
                    (h.CreatedAt - p.CreatedAt).Duration() > createdAtTolerance)
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return overlap;
            }
        }

        return 0;
    }

    private static IEnumerable<(DateTimeOffset CreatedAt, string Text)> ExtractCompletedMessages(IEnumerable<RunEventEnvelope> events)
    {
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

            yield return (env.CreatedAt, text);
        }
    }

    private static void EnqueueCompletedMessage(Queue<object> queue, int maxCount, DateTimeOffset createdAt, string text)
    {
        if (queue.Count == maxCount)
        {
            queue.Dequeue();
        }

        queue.Enqueue(new { createdAt, text });
    }

    private static Run WithCodexLastNotificationAt(Run run, RunNotificationBacklog backlog)
    {
        var last = backlog.GetLastNotificationAt(run.RunId);
        if (last is null)
        {
            return run;
        }

        if (run.CodexLastNotificationAt is null || last.Value > run.CodexLastNotificationAt.Value)
        {
            return run with { CodexLastNotificationAt = last };
        }

        return run;
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
