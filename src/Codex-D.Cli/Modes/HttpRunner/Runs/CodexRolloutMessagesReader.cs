using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexD.HttpRunner.Runs;

internal static class CodexRolloutMessagesReader
{
    public static async Task<IReadOnlyList<RolloutAssistantMessage>> ReadAssistantMessagesAsync(
        string rolloutPath,
        int maxCount,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rolloutPath);

        maxCount = Math.Max(1, Math.Min(maxCount, 50));

        var queue = new Queue<RolloutAssistantMessage>(maxCount);

        await using var stream = File.Open(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
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

                if (!TryGetTimestamp(root, out var ts))
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
                    if (payload.TryGetProperty("type", out var msgTypeEl) &&
                        msgTypeEl.ValueKind == JsonValueKind.String &&
                        string.Equals(msgTypeEl.GetString(), "agent_message", StringComparison.Ordinal) &&
                        payload.TryGetProperty("message", out var messageEl) &&
                        messageEl.ValueKind == JsonValueKind.String)
                    {
                        Enqueue(queue, maxCount, new RolloutAssistantMessage(ts, messageEl.GetString() ?? string.Empty));
                    }
                }
                else if (string.Equals(type, "response_item", StringComparison.Ordinal))
                {
                    if (payload.TryGetProperty("type", out var itemTypeEl) &&
                        itemTypeEl.ValueKind == JsonValueKind.String &&
                        string.Equals(itemTypeEl.GetString(), "message", StringComparison.Ordinal) &&
                        payload.TryGetProperty("role", out var roleEl) &&
                        roleEl.ValueKind == JsonValueKind.String &&
                        string.Equals(roleEl.GetString(), "assistant", StringComparison.Ordinal) &&
                        payload.TryGetProperty("content", out var contentEl) &&
                        contentEl.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in contentEl.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            if (!item.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var kind = t.GetString();
                            if (!string.Equals(kind, "output_text", StringComparison.Ordinal) &&
                                !string.Equals(kind, "outputText", StringComparison.Ordinal))
                            {
                                continue;
                            }

                            if (!item.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var text = textEl.GetString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                sb.Append(text);
                            }
                        }

                        var msg = sb.ToString();
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            Enqueue(queue, maxCount, new RolloutAssistantMessage(ts, msg));
                        }
                    }
                }
            }
            catch
            {
                // ignore parse errors
            }
        }

        return queue.ToArray();
    }

    private static void Enqueue(Queue<RolloutAssistantMessage> queue, int maxCount, RolloutAssistantMessage item)
    {
        if (queue.Count == maxCount)
        {
            queue.Dequeue();
        }
        queue.Enqueue(item);
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

internal readonly record struct RolloutAssistantMessage(DateTimeOffset CreatedAt, string Text);

