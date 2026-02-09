using System.Collections.Concurrent;
using System.Text.Json;
using CodexD.HttpRunner.Contracts;
using CodexD.Shared.Paths;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexD.HttpRunner.Runs;

public sealed class RunManager
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RunStore _store;
    private readonly RunEventBroadcaster _broadcaster;
    private readonly IRunExecutor _executor;
    private readonly CancellationToken _appStopping;
    private readonly ILogger<RunManager> _logger;

    private readonly ConcurrentDictionary<Guid, ActiveRun> _active = new();

    public RunManager(
        RunStore store,
        RunEventBroadcaster broadcaster,
        IRunExecutor executor,
        IHostApplicationLifetime lifetime,
        ILogger<RunManager> logger)
    {
        _store = store;
        _broadcaster = broadcaster;
        _executor = executor;
        _appStopping = lifetime.ApplicationStopping;
        _logger = logger;
    }

    public async Task<Run> CreateAndStartAsync(CreateRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cwd = NormalizeAndValidateCwd(request.Cwd);
        var kind = RunKinds.Normalize(request.Kind);

        var created = await _store.CreateAsync(
            cwd: cwd,
            kind: kind,
            review: request.Review,
            model: string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
            sandbox: string.IsNullOrWhiteSpace(request.Sandbox) ? null : request.Sandbox.Trim(),
            approvalPolicy: string.IsNullOrWhiteSpace(request.ApprovalPolicy) ? null : request.ApprovalPolicy.Trim(),
            ct);

        var record = created.Run;
        await AppendAndPublishAsync(record.RunId, "run.meta", record, ct);

        var active = new ActiveRun(record.RunId);
        if (!_active.TryAdd(record.RunId, active))
        {
            return record;
        }

        _ = Task.Run(() => ExecuteAsync(active, record, request.Prompt, _appStopping), CancellationToken.None);

        return record;
    }

    public async Task<bool> TryInterruptAsync(Guid runId, CancellationToken ct)
    {
        if (!_active.TryGetValue(runId, out var active))
        {
            return false;
        }

        var interrupt = active.Interrupt;
        if (interrupt is null)
        {
            return false;
        }

        try
        {
            await interrupt(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to interrupt run. runId={RunId}", runId);
            return false;
        }
    }

    public async Task FailAllInProgressAsync(string reason, CancellationToken ct)
    {
        foreach (var kvp in _active.ToArray())
        {
            var runId = kvp.Key;
            try
            {
                var record = await _store.TryGetAsync(runId, ct);
                if (record is null)
                {
                    continue;
                }

                if (record.Status is RunStatuses.Succeeded or RunStatuses.Failed or RunStatuses.Interrupted)
                {
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                var failed = record with
                {
                    Status = RunStatuses.Failed,
                    CompletedAt = now,
                    Error = reason
                };

                await _store.UpdateAsync(runId, failed, ct);
                await AppendAndPublishAsync(runId, "run.completed", failed, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark run as failed after runtime restart. runId={RunId}", runId);
            }
            finally
            {
                _active.TryRemove(runId, out _);
            }
        }
    }

    private async Task ExecuteAsync(ActiveRun active, Run record, string prompt, CancellationToken outerCt)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        active.Cancellation = runCts;

        var runId = record.RunId;
        try
        {
            var now = DateTimeOffset.UtcNow;
            record = record with { Status = RunStatuses.Running, StartedAt = now };
            await _store.UpdateAsync(runId, record, runCts.Token);
            await AppendAndPublishAsync(runId, "run.meta", record, runCts.Token);

            Task PublishNotificationAsync(string method, JsonElement @params, CancellationToken ct) =>
                AppendAndPublishAsync(runId, "codex.notification", new { method, @params }, ct);

            async Task SetCodexIdsAsync(string threadId, string? turnId, CancellationToken ct)
            {
                record = record with { CodexThreadId = threadId, CodexTurnId = turnId ?? record.CodexTurnId };

                await _store.UpdateAsync(runId, record, ct);
                await AppendAndPublishAsync(runId, "run.meta", record, ct);
            }

            void SetInterrupt(Func<CancellationToken, Task> interrupt) => active.Interrupt = interrupt;

            var ctx = new RunExecutionContext
            {
                RunId = runId,
                Cwd = record.Cwd,
                Prompt = prompt,
                Kind = record.Kind,
                Review = record.Review,
                Model = record.Model,
                Sandbox = record.Sandbox,
                ApprovalPolicy = record.ApprovalPolicy,
                PublishNotificationAsync = PublishNotificationAsync,
                SetCodexIdsAsync = SetCodexIdsAsync,
                SetInterrupt = SetInterrupt
            };

            var result = await _executor.ExecuteAsync(ctx, runCts.Token);

            var completed = record with
            {
                Status = result.Status,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = result.Error
            };

            await _store.UpdateAsync(runId, completed, runCts.Token);
            await AppendAndPublishAsync(runId, "run.completed", completed, runCts.Token);
        }
        catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
        {
            // ignore outer cancellation
        }
        catch (OperationCanceledException)
        {
            // run cancelled
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run failed. runId={RunId}", runId);
            var now = DateTimeOffset.UtcNow;
            var failed = record with
            {
                Status = RunStatuses.Failed,
                CompletedAt = now,
                Error = ex.Message
            };
            try
            {
                await _store.UpdateAsync(runId, failed, CancellationToken.None);
                await AppendAndPublishAsync(runId, "run.completed", failed, CancellationToken.None);
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            _active.TryRemove(runId, out _);
        }
    }

    private async Task AppendAndPublishAsync<T>(Guid runId, string type, T data, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToElement(data, Json);
        var envelope = new RunEventEnvelope
        {
            Type = type,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = json
        };

        await _store.AppendEventAsync(runId, envelope, ct);
        _broadcaster.Publish(runId, envelope);
    }

    private static string NormalizeAndValidateCwd(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        var full = Path.GetFullPath(cwd);
        if (!Directory.Exists(full))
        {
            throw new ArgumentException($"Cwd does not exist: {full}", nameof(cwd));
        }

        return PathPolicy.TrimTrailingSeparators(full);
    }

    private sealed class ActiveRun
    {
        public ActiveRun(Guid runId)
        {
            RunId = runId;
        }

        public Guid RunId { get; }
        public CancellationTokenSource? Cancellation { get; set; }
        public Func<CancellationToken, Task>? Interrupt { get; set; }
    }
}
