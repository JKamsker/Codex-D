namespace CodexD.HttpRunner.State;

public static class StatePaths
{
    public const int DEFAULT_FOREGROUND_PORT = 8787;
    public const int DEFAULT_FOREGROUND_PORT_DEV = 8788;
    public const int DEFAULT_DAEMON_PORT = 0;
    public const string DEFAULT_FOREGROUND_STATE_DIR_NAME = ".codex-d";

    public static string GetDefaultStateDirectory()
    {
        return GetForegroundStateDir(Directory.GetCurrentDirectory());
    }

    public static string GetDaemonBaseDir()
    {
        var baseDir = GetLocalAppDataDirOrFallback();
        return Path.Combine(baseDir, "codex-d", "daemon");
    }

    public static string GetDaemonDevStateDir()
    {
        var baseDir = GetLocalAppDataDirOrFallback();
        return Path.Combine(baseDir, "codex-d", "daemon-dev");
    }

    public static string GetDaemonBinDir() =>
        Path.Combine(GetDaemonBaseDir(), "bin");

    public static string GetDaemonStateDir() =>
        Path.Combine(GetDaemonBaseDir(), "config");

    public static string GetDefaultDaemonStateDir(bool isDev) =>
        isDev ? GetDaemonDevStateDir() : GetDaemonStateDir();

    public static string GetDaemonBinDirForStateDir(string daemonStateDir)
    {
        var full = Path.GetFullPath(daemonStateDir);
        var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leaf, "config", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return Path.Combine(parent, "bin");
            }
        }

        return Path.Combine(full, "bin");
    }

    public static int GetDefaultForegroundPort(bool isDev) =>
        isDev ? DEFAULT_FOREGROUND_PORT_DEV : DEFAULT_FOREGROUND_PORT;

    public static string GetForegroundStateDir(string cwd)
    {
        var fullCwd = Path.GetFullPath(cwd);
        return Path.Combine(fullCwd, DEFAULT_FOREGROUND_STATE_DIR_NAME);
    }

    public static string GetDaemonRuntimeFilePath() =>
        Path.Combine(GetDaemonStateDir(), "daemon.runtime.json");

    public static string IdentityFile(string stateDirectory) =>
        Path.Combine(stateDirectory, "identity.json");

    public static string RunsRoot(string stateDirectory) =>
        Path.Combine(stateDirectory, "runs");

    public static string RunsIndexFile(string stateDirectory) =>
        Path.Combine(RunsRoot(stateDirectory), "index.jsonl");

    private static string GetLocalAppDataDirOrFallback()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = AppContext.BaseDirectory;
        }

        return baseDir;
    }
}
