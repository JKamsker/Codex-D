namespace CodexD.HttpRunner.State;

public static class StatePaths
{
    public static string GetDefaultStateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = AppContext.BaseDirectory;
            }

            return Path.Combine(baseDir, "codex-d");
        }

        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgStateHome))
        {
            return Path.Combine(xdgStateHome, "codex-d");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = AppContext.BaseDirectory;
        }

        return Path.Combine(home, ".local", "state", "codex-d");
    }

    public static string IdentityFile(string stateDirectory) =>
        Path.Combine(stateDirectory, "identity.json");

    public static string RunsRoot(string stateDirectory) =>
        Path.Combine(stateDirectory, "runs");

    public static string RunsIndexFile(string stateDirectory) =>
        Path.Combine(RunsRoot(stateDirectory), "index.jsonl");
}
