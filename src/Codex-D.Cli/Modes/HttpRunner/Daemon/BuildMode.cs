namespace CodexD.HttpRunner.Daemon;

internal static class BuildMode
{
    internal const string DEV_MODE_ENV = "CODEX_D_DEV_MODE";

    public static bool IsDev()
    {
        var env = Environment.GetEnvironmentVariable(DEV_MODE_ENV);
        if (string.Equals(env?.Trim(), "1", StringComparison.Ordinal) ||
            string.Equals(env?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

#if DEBUG
        return true;
#else
        return false;
#endif
    }
}

