namespace CodexWebUi.Runner.Shared.Paths;

public static class PathPolicy
{
    public static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static void EnsureWithinRoots(string fullPath, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
        {
            return;
        }

        var path = WithTrailingSeparator(Path.GetFullPath(fullPath));

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var rootFull = WithTrailingSeparator(Path.GetFullPath(root));

            if (path.StartsWith(rootFull, GetComparison()))
            {
                return;
            }
        }

        throw new PathPolicyException("forbidden", "Path is outside configured workspace roots.");
    }

    private static string WithTrailingSeparator(string path)
    {
        var trimmed = TrimTrailingSeparators(path);
        return trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar)
            ? trimmed
            : trimmed + Path.DirectorySeparatorChar;
    }

    private static StringComparison GetComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
