using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexD.HttpRunner.Contracts;

namespace CodexD.HttpRunner.Runs;

public sealed class RunNotificationBacklog
{
    private const int MaxBufferedNotificationsPerRun = 50_000;
    private static readonly TimeSpan RolloutRefreshMinInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaterializationLag = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, BacklogState> _states = new();

    public void SetRolloutPath(Guid runId, string? rolloutPath)
    {
        rolloutPath = CodexRolloutPathNormalizer.Normalize(rolloutPath);
        if (string.IsNullOrWhiteSpace(rolloutPath))
        {
            return;
        }

        var state = _states.GetOrAdd(runId, _ => new BacklogState());
        lock (state.Gate)
        {
            if (!string.Equals(state.RolloutPath, rolloutPath, StringComparison.Ordinal))
            {
                state.RolloutPath = rolloutPath;
                state.MaterializedAt = null;
                state.NextRefreshAtUtc = DateTimeOffset.MinValue;
            }
        }
    }

    public void Add(Guid runId, RunEventEnvelope envelope)
    {
        var state = _states.GetOrAdd(runId, _ => new BacklogState());
        lock (state.Gate)
        {
            state.Events.Enqueue(envelope);
            while (state.Events.Count > MaxBufferedNotificationsPerRun)
            {
                state.Events.Dequeue();
            }

            TryRefreshMaterializationLocked(state);
            PruneLocked(state);
        }
    }

    public IReadOnlyList<RunEventEnvelope> SnapshotAfter(Guid runId, DateTimeOffset? afterExclusive)
    {
        if (!_states.TryGetValue(runId, out var state))
        {
            return Array.Empty<RunEventEnvelope>();
        }

        lock (state.Gate)
        {
            if (state.Events.Count == 0)
            {
                return Array.Empty<RunEventEnvelope>();
            }

            if (afterExclusive is null)
            {
                return state.Events.ToArray();
            }

            return state.Events.Where(e => e.CreatedAt > afterExclusive.Value).ToArray();
        }
    }

    public IReadOnlyList<RunEventEnvelope> SnapshotPending(Guid runId)
    {
        if (!_states.TryGetValue(runId, out var state))
        {
            return Array.Empty<RunEventEnvelope>();
        }

        lock (state.Gate)
        {
            TryRefreshMaterializationLocked(state);
            PruneLocked(state);
            return state.Events.ToArray();
        }
    }

    private static void TryRefreshMaterializationLocked(BacklogState state)
    {
        if (string.IsNullOrWhiteSpace(state.RolloutPath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < state.NextRefreshAtUtc)
        {
            return;
        }

        state.NextRefreshAtUtc = now + RolloutRefreshMinInterval;

        DateTimeOffset? max = null;
        try
        {
            max = TryReadMaxTimestampFromTail(state.RolloutPath);
        }
        catch
        {
            max = null;
        }

        if (max is null)
        {
            return;
        }

        state.MaterializedAt = state.MaterializedAt is null || max.Value > state.MaterializedAt.Value
            ? max
            : state.MaterializedAt;
    }

    private static DateTimeOffset? TryReadMaxTimestampFromTail(string rolloutPath)
    {
        if (!File.Exists(rolloutPath))
        {
            return null;
        }

        using var stream = File.Open(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return null;
        }

        const int maxBytes = 512 * 1024;
        var toRead = (int)Math.Min(stream.Length, maxBytes);
        stream.Seek(-toRead, SeekOrigin.End);

        var buf = new byte[toRead];
        var read = stream.Read(buf, 0, toRead);
        if (read <= 0)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(buf, 0, read);
        var lines = text.Split(['\n'], StringSplitOptions.RemoveEmptyEntries);

        DateTimeOffset? max = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var tsRaw = tsEl.GetString();
                if (string.IsNullOrWhiteSpace(tsRaw))
                {
                    continue;
                }

                if (!DateTimeOffset.TryParse(
                        tsRaw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var ts))
                {
                    continue;
                }

                max = max is null || ts > max.Value ? ts : max;
            }
            catch
            {
                // ignore parse errors
            }
        }

        return max;
    }

    private static void PruneLocked(BacklogState state)
    {
        if (state.MaterializedAt is null)
        {
            return;
        }

        var threshold = state.MaterializedAt.Value - MaterializationLag;
        while (state.Events.Count > 0)
        {
            var peek = state.Events.Peek();
            if (peek.CreatedAt > threshold)
            {
                break;
            }

            state.Events.Dequeue();
        }
    }

    private sealed class BacklogState
    {
        public object Gate { get; } = new();
        public Queue<RunEventEnvelope> Events { get; } = new();
        public string? RolloutPath { get; set; }
        public DateTimeOffset? MaterializedAt { get; set; }
        public DateTimeOffset NextRefreshAtUtc { get; set; }
    }
}

