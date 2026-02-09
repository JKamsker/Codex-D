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
        if (string.IsNullOrWhiteSpace(delta))
        {
            return;
        }

        var trimmed = delta.Trim();
        if (string.Equals(trimmed, "thinking", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "final", StringComparison.OrdinalIgnoreCase))
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

        var state = _states.GetOrAdd(runId, _ => new RollupState());

        await state.Gate.WaitAsync(ct);
        try
        {
            foreach (var record in ConsumeDeltaIntoLineRecords(createdAt, state.Buffer, delta))
            {
                await _store.AppendRollupRecordAsync(runId, record, ct);
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static IEnumerable<RunRollupRecord> ConsumeDeltaIntoLineRecords(
        DateTimeOffset createdAt,
        StringBuilder buffer,
        string delta)
    {
        for (var i = 0; i < delta.Length; i++)
        {
            var ch = delta[i];

            if (ch == '\r')
            {
                if (i + 1 < delta.Length && delta[i + 1] == '\n')
                {
                    i++;
                }

                yield return BuildLineRecord(createdAt, buffer, endsWithNewline: true);
                buffer.Clear();
                continue;
            }

            if (ch == '\n')
            {
                yield return BuildLineRecord(createdAt, buffer, endsWithNewline: true);
                buffer.Clear();
                continue;
            }

            buffer.Append(ch);
        }
    }

    private static RunRollupRecord BuildLineRecord(DateTimeOffset createdAt, StringBuilder buffer, bool endsWithNewline)
    {
        var text = buffer.ToString();
        return new RunRollupRecord
        {
            Type = "outputLine",
            CreatedAt = createdAt,
            Source = "commandExecution",
            Text = text,
            EndsWithNewline = endsWithNewline,
            IsControl = false
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

        await _store.AppendRollupRecordAsync(
            runId,
            new RunRollupRecord
            {
                Type = "outputLine",
                CreatedAt = createdAt,
                Source = "commandExecution",
                Text = text,
                EndsWithNewline = false,
                IsControl = false
            },
            ct);

        state.Buffer.Clear();
    }

    private sealed class RollupState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public StringBuilder Buffer { get; } = new();
    }
}
