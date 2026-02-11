namespace CodexD.HttpRunner.Runs;

internal static class CodexRolloutPathNormalizer
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.Trim();
        if (path.Length == 0)
        {
            return null;
        }

        // Codex app-server often returns Windows extended-length paths (\\?\C:\...).
        // For our usage, the normalized win32 path is sufficient and avoids surprising serialization.
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(extendedPrefix, StringComparison.Ordinal))
        {
            path = path[extendedPrefix.Length..];
        }

        return path;
    }
}

