using System.Collections.Concurrent;
using System.Text.Json;
using CodexD.HttpRunner.Contracts;
using CodexD.Shared.Paths;
using JKToolKit.CodexSDK.AppServer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexD.HttpRunner.Runs;

public sealed class RunManager
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RunStore _store;
    private readonly RunEventBroadcaster _broadcaster;
    private readonly RunNotificationBacklog _backlog;
    private readonly IRunExecutor _executor;
    private readonly CancellationToken _appStopping;
    private readonly ILogger<RunManager> _logger;

    private readonly ConcurrentDictionary<Guid, ActiveRun> _active = new();

    public RunManager(
        RunStore store,
        RunEventBroadcaster broadcaster,
        RunNotificationBacklog backlog,
        IRunExecutor executor,
        IHostApplicationLifetime lifetime,
        ILogger<RunManager> logger)
    {
        _store = store;
        _broadcaster = broadcaster;
        _backlog = backlog;
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
            new RunCreateOptions
            {
                Cwd = cwd,
                Kind = kind,
                Review = request.Review,
                Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
                Effort = string.IsNullOrWhiteSpace(request.Effort) ? null : request.Effort.Trim(),
                Sandbox = string.IsNullOrWhiteSpace(request.Sandbox) ? null : request.Sandbox.Trim(),
                ApprovalPolicy = string.IsNullOrWhiteSpace(request.ApprovalPolicy) ? null : request.ApprovalPolicy.Trim()
            },
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

    public async Task<bool> TryStopAsync(Guid runId, CancellationToken ct)
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

        Run? record;
        try
        {
            record = await _store.TryGetAsync(runId, ct);
            if (record is null)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        active.RequestStop();

        try
        {
            await interrupt(ct);
            return true;
        }
        catch (Exception ex)
        {
            active.ClearStopRequested();
            _logger.LogWarning(ex, "Failed to stop run. runId={RunId}", runId);
            return false;
        }
    }

    public async Task<Run?> ResumeAsync(Guid runId, string prompt, string? effort, CancellationToken ct)
    {
        var record = await _store.TryGetAsync(runId, ct);
        if (record is null)
        {
            return null;
        }

        if (IsTerminalStatus(record.Status))
        {
            return null;
        }

        var kind = RunKinds.Normalize(record.Kind);
        if (kind != RunKinds.Exec)
        {
            return null;
        }

        var normalizedEffort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();

        var queued = record with
        {
            Status = RunStatuses.Queued,
            CompletedAt = null,
            Error = null,
            Effort = normalizedEffort ?? record.Effort
        };

        var active = new ActiveRun(runId);
        if (!_active.TryAdd(runId, active))
        {
            return null;
        }

        try
        {
            await _store.UpdateAsync(runId, queued, ct);
            await AppendAndPublishAsync(runId, "run.meta", queued, ct);
        }
        catch
        {
            _active.TryRemove(runId, out _);
            throw;
        }

        _ = Task.Run(() => ExecuteAsync(active, queued, prompt, _appStopping), CancellationToken.None);

        return queued;
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

                if (IsTerminalStatus(record.Status) || string.Equals(record.Status, RunStatuses.Paused, StringComparison.Ordinal))
                {
                    continue;
                }

                var lastFromBacklog = _backlog.GetLastNotificationAt(runId);
                var bestLast = record.CodexLastNotificationAt;
                if (lastFromBacklog is not null && (bestLast is null || lastFromBacklog.Value > bestLast.Value))
                {
                    bestLast = lastFromBacklog;
                }

                var now = DateTimeOffset.UtcNow;
                var failed = record with
                {
                    Status = RunStatuses.Failed,
                    CompletedAt = now,
                    Error = reason,
                    CodexLastNotificationAt = bestLast
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

    public async Task PauseAllInProgressAsync(string reason, CancellationToken ct)
    {
        foreach (var kvp in _active.ToArray())
        {
            var runId = kvp.Key;
            var active = kvp.Value;

            Run? record;
            try
            {
                record = await _store.TryGetAsync(runId, ct);
                if (record is null)
                {
                    _active.TryRemove(runId, out _);
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load run record while pausing in-progress runs. runId={RunId}", runId);
                continue;
            }

            if (!string.Equals(RunKinds.Normalize(record.Kind), RunKinds.Exec, StringComparison.Ordinal))
            {
                continue;
            }

            active.RequestPause();
            try { active.Cancellation?.Cancel(); } catch { }

            try
            {
                if (IsTerminalStatus(record.Status) || string.Equals(record.Status, RunStatuses.Paused, StringComparison.Ordinal))
                {
                    continue;
                }

                var lastFromBacklog = _backlog.GetLastNotificationAt(runId);
                var bestLast = record.CodexLastNotificationAt;
                if (lastFromBacklog is not null && (bestLast is null || lastFromBacklog.Value > bestLast.Value))
                {
                    bestLast = lastFromBacklog;
                }

                var paused = record with
                {
                    Status = RunStatuses.Paused,
                    CompletedAt = null,
                    Error = reason,
                    CodexLastNotificationAt = bestLast
                };

                await _store.UpdateAsync(runId, paused, ct);
                await AppendAndPublishAsync(runId, "run.paused", paused, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark run as paused after runtime restart. runId={RunId}", runId);
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
        DateTimeOffset? lastNotificationAt = record.CodexLastNotificationAt;
        try
        {
            var now = DateTimeOffset.UtcNow;
            record = record with { Status = RunStatuses.Running, StartedAt = now };
            await _store.UpdateAsync(runId, record, runCts.Token);
            await AppendAndPublishAsync(runId, "run.meta", record, runCts.Token);

            Task PublishNotificationAsync(string method, JsonElement @params, CancellationToken ct)
            {
                lastNotificationAt = DateTimeOffset.UtcNow;
                return AppendAndPublishAsync(runId, "codex.notification", new { method, @params }, ct);
            }

            async Task SetCodexIdsAsync(string threadId, string? turnId, string? rolloutPath, CancellationToken ct)
            {
                var normalizedRolloutPath = CodexRolloutPathNormalizer.Normalize(rolloutPath);

                record = record with
                {
                    CodexThreadId = threadId,
                    CodexTurnId = turnId ?? record.CodexTurnId,
                    CodexRolloutPath = normalizedRolloutPath ?? record.CodexRolloutPath
                };

                await _store.UpdateAsync(runId, record, ct);
                await AppendAndPublishAsync(runId, "run.meta", record, ct);

                _backlog.SetRolloutPath(runId, record.CodexRolloutPath);
            }

            void SetInterrupt(Func<CancellationToken, Task> interrupt) => active.Interrupt = interrupt;

            var ctx = new RunExecutionContext
            {
                RunId = runId,
                Cwd = record.Cwd,
                Prompt = prompt,
                CodexThreadId = record.CodexThreadId,
                Kind = record.Kind,
                Review = record.Review,
                Model = record.Model,
                Effort = record.Effort,
                Sandbox = record.Sandbox,
                ApprovalPolicy = record.ApprovalPolicy,
                PublishNotificationAsync = PublishNotificationAsync,
                SetCodexIdsAsync = SetCodexIdsAsync,
                SetInterrupt = SetInterrupt
            };

            var result = await _executor.ExecuteAsync(ctx, runCts.Token);

            if (active.StopRequested &&
                string.Equals(result.Status, RunStatuses.Interrupted, StringComparison.Ordinal))
            {
                var paused = record with
                {
                    Status = RunStatuses.Paused,
                    CompletedAt = null,
                    Error = null,
                    CodexLastNotificationAt = lastNotificationAt
                };

                await _store.UpdateAsync(runId, paused, CancellationToken.None);
                await AppendAndPublishAsync(runId, "run.paused", paused, CancellationToken.None);
                return;
            }

            var completed = record with
            {
                Status = result.Status,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = result.Error,
                CodexLastNotificationAt = lastNotificationAt
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
            // run cancelled (PauseAllInProgressAsync may have already finalized state)
        }
        catch (CodexAppServerDisconnectedException ex) when (!string.IsNullOrWhiteSpace(record.CodexThreadId))
        {
            _logger.LogWarning(ex, "Codex runtime disconnected; pausing run. runId={RunId}", runId);
            var paused = record with
            {
                Status = RunStatuses.Paused,
                CompletedAt = null,
                Error = "codex runtime disconnected",
                CodexLastNotificationAt = lastNotificationAt
            };
            try
            {
                await _store.UpdateAsync(runId, paused, CancellationToken.None);
                await AppendAndPublishAsync(runId, "run.paused", paused, CancellationToken.None);
            }
            catch
            {
                // ignore
            }
        }
        catch (Exception ex)
        {
            if (active.PauseRequested)
            {
                return;
            }

            _logger.LogWarning(ex, "Run failed. runId={RunId}", runId);
            var now = DateTimeOffset.UtcNow;
            var failed = record with
            {
                Status = RunStatuses.Failed,
                CompletedAt = now,
                Error = ex.Message,
                CodexLastNotificationAt = lastNotificationAt
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

        _broadcaster.Publish(runId, envelope);

        try
        {
            await _store.AppendRawEventAsync(runId, envelope, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist raw run event. runId={RunId} type={Type}", runId, type);
        }

        if (string.Equals(type, "codex.notification", StringComparison.Ordinal))
        {
            _backlog.Add(runId, envelope);
        }
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

    private static bool IsTerminalStatus(string status) =>
        string.Equals(status, RunStatuses.Succeeded, StringComparison.Ordinal) ||
        string.Equals(status, RunStatuses.Failed, StringComparison.Ordinal) ||
        string.Equals(status, RunStatuses.Interrupted, StringComparison.Ordinal);

    private sealed class ActiveRun
    {
        public ActiveRun(Guid runId)
        {
            RunId = runId;
        }

        public Guid RunId { get; }
        public CancellationTokenSource? Cancellation { get; set; }
        public Func<CancellationToken, Task>? Interrupt { get; set; }

        private int _stopRequested;
        private int _pauseRequested;

        public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;
        public bool PauseRequested => Volatile.Read(ref _pauseRequested) != 0;

        public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);
        public void ClearStopRequested() => Interlocked.Exchange(ref _stopRequested, 0);
        public void RequestPause() => Interlocked.Exchange(ref _pauseRequested, 1);
    }
}
