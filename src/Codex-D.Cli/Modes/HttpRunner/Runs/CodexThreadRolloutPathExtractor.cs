using System.Text.Json;

namespace CodexD.HttpRunner.Runs;

internal static class CodexThreadRolloutPathExtractor
{
    public static string? TryExtract(JsonElement threadStartOrResumeResult)
    {
        if (threadStartOrResumeResult.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!threadStartOrResumeResult.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!thread.TryGetProperty("path", out var pathEl) || pathEl.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            return null;
        }

        var raw = pathEl.ValueKind == JsonValueKind.String ? pathEl.GetString() : null;
        return CodexRolloutPathNormalizer.Normalize(raw);
    }
}

