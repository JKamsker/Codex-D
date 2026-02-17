using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexD.HttpRunner.Contracts;

namespace CodexD.HttpRunner.Runs;

internal static class ThinkingSummaries
{
    public static IReadOnlyList<string> FromRawEvents(IReadOnlyList<RunEventEnvelope> events) =>
        BuildSummaryItems(rolloutPath: null, events, CancellationToken.None).Select(x => x.Text).ToArray();

    public static IReadOnlyList<string> FromCodexRollout(string rolloutPath, int? _tailEvents, CancellationToken ct) =>
        BuildSummaryItems(rolloutPath, events: null, ct).Select(x => x.Text).ToArray();

    public static IReadOnlyList<string> FromCodexRolloutThenRawEvents(
        string rolloutPath,
        IReadOnlyList<RunEventEnvelope> extraEvents,
        CancellationToken ct) =>
        BuildSummaryItems(rolloutPath, extraEvents, ct).Select(x => x.Text).ToArray();

    public static IReadOnlyList<ThinkingSummaryItem> FromRawEventsWithTimestamps(IReadOnlyList<RunEventEnvelope> events) =>
        BuildSummaryItems(rolloutPath: null, events, CancellationToken.None);

    public static IReadOnlyList<ThinkingSummaryItem> FromCodexRolloutThenRawEventsWithTimestamps(
        string rolloutPath,
        IReadOnlyList<RunEventEnvelope> extraEvents,
        CancellationToken ct) =>
        BuildSummaryItems(rolloutPath, extraEvents, ct);

    private static IReadOnlyList<ThinkingSummaryItem> BuildSummaryItems(
        string? rolloutPath,
        IReadOnlyList<RunEventEnvelope>? events,
        CancellationToken ct)
    {
        var summaries = new List<ThinkingSummaryItem>();
        var last = string.Empty;
        var inThinking = false;

        if (!string.IsNullOrWhiteSpace(rolloutPath))
        {
            foreach (var (createdAt, delta) in ReadOutputDeltaEventsFromCodexRollout(rolloutPath, ct))
            {
                AddSummariesFromDelta(createdAt, delta, summaries, ref last, ref inThinking);
            }
        }

        if (events is not null)
        {
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

                AddSummariesFromDelta(env.CreatedAt, delta, summaries, ref last, ref inThinking);
            }
        }

        return summaries;
    }

    private static void AddSummariesFromDelta(
        DateTimeOffset createdAt,
        string delta,
        List<ThinkingSummaryItem> summaries,
        ref string last,
        ref bool inThinking)
    {
        var trimmed = delta.Trim();
        if (string.Equals(trimmed, "thinking", StringComparison.OrdinalIgnoreCase))
        {
            inThinking = true;
            return;
        }

        if (string.Equals(trimmed, "final", StringComparison.OrdinalIgnoreCase))
        {
            inThinking = false;
            return;
        }

        var maybeThinking = inThinking || delta.Contains("thinking", StringComparison.OrdinalIgnoreCase);
        if (!maybeThinking)
        {
            return;
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

            summaries.Add(new ThinkingSummaryItem { CreatedAt = createdAt, Text = summary });
            last = summary;
        }
    }

    private static IReadOnlyList<(DateTimeOffset CreatedAt, string Delta)> ReadOutputDeltaEventsFromCodexRollout(string rolloutPath, CancellationToken ct)
    {
        var deltas = new List<(DateTimeOffset CreatedAt, string Delta)>();
        if (string.IsNullOrWhiteSpace(rolloutPath) || !File.Exists(rolloutPath))
        {
            return deltas;
        }

        using var stream = File.Open(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetTimestamp(root, out var createdAt))
                {
                    continue;
                }

                if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = typeEl.GetString();
                if (string.Equals(type, "event_msg", StringComparison.Ordinal))
                {
                    if (!payload.TryGetProperty("type", out var msgTypeEl) || msgTypeEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!string.Equals(msgTypeEl.GetString(), "exec_command_output_delta", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var delta = TryDecodeBase64Chunk(payload) ?? TryGetString(payload, "delta");
                    if (!string.IsNullOrEmpty(delta))
                    {
                        deltas.Add((createdAt, delta));
                    }
                }
                else if (string.Equals(type, "response_item", StringComparison.Ordinal))
                {
                    if (!payload.TryGetProperty("type", out var itemTypeEl) || itemTypeEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!string.Equals(itemTypeEl.GetString(), "function_call_output", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!payload.TryGetProperty("output", out var outputEl) || outputEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var output = outputEl.GetString();
                    if (!string.IsNullOrEmpty(output))
                    {
                        deltas.Add((createdAt, output));
                    }
                }
            }
            catch
            {
                // ignore parse errors
            }
        }

        return deltas;
    }

    private static string? TryDecodeBase64Chunk(JsonElement payload)
    {
        var raw = TryGetString(payload, "chunk");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(raw);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!obj.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static bool TryGetTimestamp(JsonElement root, out DateTimeOffset ts)
    {
        ts = default;

        if (!root.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = tsEl.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out ts);
    }
}
