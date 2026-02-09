using System.Collections.Concurrent;
using System.Text;
using CodexD.HttpRunner.Contracts;

namespace CodexD.HttpRunner.Runs;

public sealed class RunRollupWriter
{
    private readonly RunStore _store;
    private readonly ConcurrentDictionary<Guid, RollupState> _states = new();

    public RunRollupWriter(RunStore store)
    {
        _store = store;
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
                return;
            }
        }

        var state = _states.GetOrAdd(runId, _ => new RollupState());

        await state.Gate.WaitAsync(ct);
        try
        {
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

                    var record = BuildLineRecord(createdAt, state.Buffer, endsWithNewline: true);
                    await _store.AppendRollupRecordAsync(runId, record, ct);
                    state.Buffer.Clear();
                    continue;
                }

                if (ch == '\n')
                {
                    var record = BuildLineRecord(createdAt, state.Buffer, endsWithNewline: true);
                    await _store.AppendRollupRecordAsync(runId, record, ct);
                    state.Buffer.Clear();
                    continue;
                }

                state.Buffer.Append(ch);
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static RunRollupRecord BuildLineRecord(DateTimeOffset createdAt, StringBuilder buffer, bool endsWithNewline)
    {
        var text = buffer.ToString();
        var trimmed = text.Trim();
        var isControl = IsControlMarker(trimmed);
        return new RunRollupRecord
        {
            Type = "outputLine",
            CreatedAt = createdAt,
            Source = "commandExecution",
            Text = isControl ? trimmed : text,
            EndsWithNewline = endsWithNewline,
            IsControl = isControl
        };
    }

    private async Task FlushUnlockedAsync(Guid runId, DateTimeOffset createdAt, RollupState state, CancellationToken ct)
    {
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
    }
}
