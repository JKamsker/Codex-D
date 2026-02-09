using System.Collections.Concurrent;
using System.Text;
using CodexD.HttpRunner.Contracts;
using Microsoft.Extensions.Logging;

namespace CodexD.HttpRunner.Runs;

public sealed class RunRollupWriter
{
    // Avoid unbounded per-run memory usage when output contains extremely long lines without terminators.
    private const int MaxBufferedChars = 64_000;

    private readonly RunStore _store;
    private readonly ILogger<RunRollupWriter> _logger;
    private readonly ConcurrentDictionary<Guid, RollupState> _states = new();

    public RunRollupWriter(RunStore store, ILogger<RunRollupWriter> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task OnEnvelopeAsync(Guid runId, RunEventEnvelope envelope, CancellationToken ct)
    {
        if (!string.Equals(envelope.Type, "codex.notification", StringComparison.Ordinal))
        {
            return;
        }

        if (RunEventDataExtractors.TryGetOutputDelta(envelope.Data, out var delta))
        {
            await OnCommandExecutionOutputDeltaAsync(runId, envelope.CreatedAt, delta ?? string.Empty, ct);
            return;
        }

        if (RunEventDataExtractors.TryGetCompletedAgentMessageText(envelope.Data, out var text))
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    await _store.AppendRollupRecordAsync(
                        runId,
                        new RunRollupRecord
                        {
                            Type = "agentMessage",
                            CreatedAt = envelope.CreatedAt,
                            Text = text
                        },
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist agent message rollup. runId={RunId}", runId);
                }
            }
        }
    }

    public async Task FlushAndStopAsync(Guid runId, DateTimeOffset createdAt, CancellationToken ct)
    {
        if (!_states.TryRemove(runId, out var state))
        {
            return;
        }

        await state.Gate.WaitAsync(ct);
        try
        {
            await FlushUnlockedAsync(runId, createdAt, state, ct);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task OnCommandExecutionOutputDeltaAsync(Guid runId, DateTimeOffset createdAt, string delta, CancellationToken ct)
    {
        if (delta.Length == 0)
        {
            return;
        }

        if (!ContainsNewline(delta))
        {
            var trimmed = delta.Trim();
            if (IsControlMarker(trimmed))
            {
                if (_states.TryGetValue(runId, out var existing))
                {
                    await existing.Gate.WaitAsync(ct);
                    try
                    {
                        if (existing.Disabled)
                        {
                            return;
                        }

                        if (existing.Buffer.Length > 0)
                        {
                            await AppendFromBufferAsync(runId, createdAt, existing, endsWithNewline: false, ct);
                        }
                    }
                    finally
                    {
                        existing.Gate.Release();
                    }
                }

                try
                {
                    await _store.AppendRollupRecordAsync(
                        runId,
                        new RunRollupRecord
                        {
                            Type = "outputLine",
                            CreatedAt = createdAt,
                            Source = "commandExecution",
                            Text = trimmed,
                            EndsWithNewline = false,
                            IsControl = true
                        },
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist control marker rollup. runId={RunId}", runId);
                }
                return;
            }
        }

        var state = _states.GetOrAdd(runId, _ => new RollupState());

        await state.Gate.WaitAsync(ct);
        try
        {
            if (state.Disabled)
            {
                return;
            }

            var startIndex = 0;
            if (state.PendingCr)
            {
                if (delta[0] == '\n')
                {
                    startIndex = 1;
                }

                state.PendingCr = false;
            }

            for (var i = startIndex; i < delta.Length; i++)
            {
                var ch = delta[i];

                if (ch == '\r')
                {
                    if (i + 1 < delta.Length && delta[i + 1] == '\n')
                    {
                        i++;
                    }
                    else if (i == delta.Length - 1)
                    {
                        state.PendingCr = true;
                    }

                    await AppendFromBufferAsync(runId, createdAt, state, endsWithNewline: true, ct);
                    if (state.Disabled)
                    {
                        return;
                    }
                    continue;
                }

                if (ch == '\n')
                {
                    await AppendFromBufferAsync(runId, createdAt, state, endsWithNewline: true, ct);
                    if (state.Disabled)
                    {
                        return;
                    }
                    continue;
                }

                state.Buffer.Append(ch);
                if (state.Buffer.Length >= MaxBufferedChars)
                {
                    await AppendFromBufferAsync(runId, createdAt, state, endsWithNewline: false, ct);
                    if (state.Disabled)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task AppendFromBufferAsync(
        Guid runId,
        DateTimeOffset createdAt,
        RollupState state,
        bool endsWithNewline,
        CancellationToken ct)
    {
        var text = state.Buffer.ToString();
        var trimmed = text.Trim();
        var isControl = IsControlMarker(trimmed);
        var record = new RunRollupRecord
        {
            Type = "outputLine",
            CreatedAt = createdAt,
            Source = "commandExecution",
            Text = isControl ? trimmed : text,
            EndsWithNewline = endsWithNewline,
            IsControl = isControl
        };

        try
        {
            await _store.AppendRollupRecordAsync(runId, record, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.Disabled = true;
            if (!state.LoggedDisabled)
            {
                state.LoggedDisabled = true;
                _logger.LogWarning(ex, "Disabling rollup persistence for run due to write failure. runId={RunId}", runId);
            }
        }
        finally
        {
            state.Buffer.Clear();
        }
    }

    private async Task FlushUnlockedAsync(Guid runId, DateTimeOffset createdAt, RollupState state, CancellationToken ct)
    {
        if (state.Disabled)
        {
            state.Buffer.Clear();
            return;
        }

        if (state.Buffer.Length == 0)
        {
            return;
        }

        var text = state.Buffer.ToString().TrimEnd('\r');
        if (text.Length == 0)
        {
            state.Buffer.Clear();
            return;
        }

        var trimmed = text.Trim();
        var isControl = IsControlMarker(trimmed);
        try
        {
            await _store.AppendRollupRecordAsync(
                runId,
                new RunRollupRecord
                {
                    Type = "outputLine",
                    CreatedAt = createdAt,
                    Source = "commandExecution",
                    Text = isControl ? trimmed : text,
                    EndsWithNewline = false,
                    IsControl = isControl
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush rollup buffer. runId={RunId}", runId);
        }

        state.Buffer.Clear();
    }

    private static bool ContainsNewline(string value)
        => value.Contains('\n') || value.Contains('\r');

    private static bool IsControlMarker(string value)
        => string.Equals(value, "thinking", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "final", StringComparison.OrdinalIgnoreCase);

    private sealed class RollupState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public StringBuilder Buffer { get; } = new();
        public bool PendingCr { get; set; }
        public bool Disabled { get; set; }
        public bool LoggedDisabled { get; set; }
    }
}
