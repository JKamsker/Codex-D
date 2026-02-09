using System.Text.Json;

namespace CodexD.HttpRunner.Runs;

internal static class RunEventsJsonl
{
    public static async Task<IReadOnlyList<string>> ReadLinesAsync(string path, int? tailLines, CancellationToken ct)
    {
        if (tailLines is { } n && n > 0)
        {
            var queue = new Queue<string>(n);
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (queue.Count == n)
                {
                    queue.Dequeue();
                }
                queue.Enqueue(line);
            }
            return queue.ToArray();
        }

        var list = new List<string>();
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                list.Add(line);
            }
        }
        return list;
    }

    public static bool TryGetOutputDelta(string jsonLine, out string? delta)
    {
        delta = null;

        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data))
            {
                return false;
            }

            if (!TryGetMethod(data, out var method) ||
                !string.Equals(method, "item/commandExecution/outputDelta", StringComparison.Ordinal))
            {
                return false;
            }

            if (!data.TryGetProperty("params", out var @params))
            {
                return false;
            }

            if (!@params.TryGetProperty("delta", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            delta = deltaEl.GetString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetCompletedAgentMessageText(string jsonLine, out string? text)
    {
        text = null;

        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data))
            {
                return false;
            }

            if (!TryGetMethod(data, out var method) ||
                !string.Equals(method, "item/completed", StringComparison.Ordinal))
            {
                return false;
            }

            if (!data.TryGetProperty("params", out var @params))
            {
                return false;
            }

            if (!@params.TryGetProperty("item", out var item))
            {
                return false;
            }

            if (!item.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!string.Equals(typeEl.GetString(), "agentMessage", StringComparison.Ordinal))
            {
                return false;
            }

            if (!item.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            text = textEl.GetString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetMethod(JsonElement data, out string? method)
    {
        method = null;
        if (!data.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        method = methodEl.GetString();
        return !string.IsNullOrWhiteSpace(method);
    }
}

