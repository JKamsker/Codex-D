using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexD.HttpRunner.Contracts;

namespace CodexD.HttpRunner.Runs;

public sealed class CodexRolloutRollupReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CodexRolloutReplay> ReadAsync(string rolloutPath, int? tailRecords, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rolloutPath);

        tailRecords = tailRecords is { } t && t > 0 ? t : null;

        var queue = tailRecords is { } n ? new Queue<RunRollupRecord>(n) : null;
        var list = queue is null ? new List<RunRollupRecord>() : null;

        void AddRecord(RunRollupRecord rec)
        {
            if (queue is not null)
            {
                if (queue.Count == tailRecords)
                {
                    queue.Dequeue();
                }
                queue.Enqueue(rec);
            }
            else
            {
                list!.Add(rec);
            }
        }

        var aggregator = new RollupOutputAggregator();
        DateTimeOffset? lastOutputAt = null;
        DateTimeOffset? maxAt = null;

        await using var stream = File.Open(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetTimestamp(root, out var ts))
                {
                    continue;
                }

                maxAt = maxAt is null || ts > maxAt.Value ? ts : maxAt;

                if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeEl.GetString();
                if (string.IsNullOrWhiteSpace(type) ||
                    !root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (string.Equals(type, "event_msg", StringComparison.Ordinal))
                {
                    ProcessEventMsg(payload, ts, aggregator, AddRecord, ref lastOutputAt);
                }
                else if (string.Equals(type, "response_item", StringComparison.Ordinal))
                {
                    ProcessResponseItem(payload, ts, aggregator, AddRecord, ref lastOutputAt);
                }
            }
            catch
            {
                // ignore invalid JSONL line
            }
            finally
            {
                doc?.Dispose();
            }
        }

        if (lastOutputAt is { } flushAt && aggregator.HasBufferedContent)
        {
            aggregator.Flush(flushAt, AddRecord);
        }

        var records = queue is not null ? queue.ToArray() : list!.ToArray();
        return new CodexRolloutReplay
        {
            Records = records,
            MaxCreatedAt = maxAt
        };
    }

    private static void ProcessEventMsg(
        JsonElement payload,
        DateTimeOffset createdAt,
        RollupOutputAggregator aggregator,
        Action<RunRollupRecord> add,
        ref DateTimeOffset? lastOutputAt)
    {
        if (!payload.TryGetProperty("type", out var msgTypeEl) || msgTypeEl.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var msgType = msgTypeEl.GetString();
        if (string.IsNullOrWhiteSpace(msgType))
        {
            return;
        }

        if (string.Equals(msgType, "agent_message", StringComparison.Ordinal))
        {
            if (payload.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
            {
                var message = messageEl.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    add(new RunRollupRecord { Type = "agentMessage", CreatedAt = createdAt, Text = message });
                }
            }

            return;
        }

        if (string.Equals(msgType, "exec_command_output_delta", StringComparison.Ordinal))
        {
            var delta = TryDecodeBase64Chunk(payload) ?? TryGetString(payload, "delta");
            if (!string.IsNullOrEmpty(delta))
            {
                lastOutputAt = createdAt;
                aggregator.AppendDelta(createdAt, delta, add);
            }
        }
    }

    private static void ProcessResponseItem(
        JsonElement payload,
        DateTimeOffset createdAt,
        RollupOutputAggregator aggregator,
        Action<RunRollupRecord> add,
        ref DateTimeOffset? lastOutputAt)
    {
        if (!payload.TryGetProperty("type", out var itemTypeEl) || itemTypeEl.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var itemType = itemTypeEl.GetString();
        if (string.IsNullOrWhiteSpace(itemType))
        {
            return;
        }

        if (string.Equals(itemType, "function_call_output", StringComparison.Ordinal))
        {
            if (payload.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.String)
            {
                var raw = outputEl.GetString() ?? string.Empty;
                var extracted = ExtractFunctionCallOutput(raw);
                if (extracted.Length > 0)
                {
                    lastOutputAt = createdAt;
                    aggregator.AppendDelta(createdAt, extracted, add);
                }
            }

            return;
        }

        if (string.Equals(itemType, "message", StringComparison.Ordinal))
        {
            var role = TryGetString(payload, "role");
            if (!string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                return;
            }

            if (!payload.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var sb = new StringBuilder();
            foreach (var item in contentEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var t = TryGetString(item, "type");
                if (!string.Equals(t, "output_text", StringComparison.Ordinal) &&
                    !string.Equals(t, "outputText", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = TryGetString(item, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append(text);
                }
            }

            var message = sb.ToString();
            if (!string.IsNullOrWhiteSpace(message))
            {
                add(new RunRollupRecord { Type = "agentMessage", CreatedAt = createdAt, Text = message });
            }
        }
    }

    private static string ExtractFunctionCallOutput(string raw)
    {
        raw = raw ?? string.Empty;
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var idx = raw.IndexOf("\nOutput:\n", StringComparison.Ordinal);
        if (idx >= 0)
        {
            return raw[(idx + "\nOutput:\n".Length)..];
        }

        idx = raw.IndexOf("\r\nOutput:\r\n", StringComparison.Ordinal);
        if (idx >= 0)
        {
            return raw[(idx + "\r\nOutput:\r\n".Length)..];
        }

        idx = raw.IndexOf("Output:\n", StringComparison.Ordinal);
        if (idx >= 0)
        {
            return raw[(idx + "Output:\n".Length)..];
        }

        idx = raw.IndexOf("Output:\r\n", StringComparison.Ordinal);
        if (idx >= 0)
        {
            return raw[(idx + "Output:\r\n".Length)..];
        }

        return raw;
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
}

public sealed record class CodexRolloutReplay
{
    public required IReadOnlyList<RunRollupRecord> Records { get; init; }
    public required DateTimeOffset? MaxCreatedAt { get; init; }
}
