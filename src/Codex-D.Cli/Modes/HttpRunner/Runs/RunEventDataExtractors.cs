using System.Text.Json;

namespace CodexD.HttpRunner.Runs;

internal static class RunEventDataExtractors
{
    public static bool TryGetOutputDelta(JsonElement data, out string? delta)
    {
        delta = null;

        if (!TryGetMethod(data, out var method) ||
            !string.Equals(method, "item/commandExecution/outputDelta", StringComparison.Ordinal))
        {
            return false;
        }

        if (!data.TryGetProperty("params", out var @params) || @params.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!@params.TryGetProperty("delta", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        delta = deltaEl.GetString();
        return !string.IsNullOrWhiteSpace(delta);
    }

    public static bool TryGetCompletedAgentMessageText(JsonElement data, out string? text)
    {
        text = null;

        if (!TryGetMethod(data, out var method) ||
            !string.Equals(method, "item/completed", StringComparison.Ordinal))
        {
            return false;
        }

        if (!data.TryGetProperty("params", out var @params) || @params.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!@params.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
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
        return !string.IsNullOrWhiteSpace(text);
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

