using CodexD.HttpRunner.Daemon;

namespace CodexD.Tests;

internal static class RunnerResolutionTestUtils
{
    public static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task WriteDaemonRuntimeAsync(string daemonStateDir, string baseUrl, int port, CancellationToken ct = default)
    {
        var runtime = new DaemonRuntimeInfo
        {
            BaseUrl = baseUrl,
            Listen = "127.0.0.1",
            Port = port,
            Pid = 12345,
            StartedAtUtc = DateTimeOffset.UtcNow,
            StateDir = daemonStateDir,
            Version = "test"
        };

        await DaemonRuntimeFile.WriteAtomicAsync(Path.Combine(daemonStateDir, "daemon.runtime.json"), runtime, ct);
    }

    public static async Task WriteIdentityAsync(string stateDir, string token, CancellationToken ct = default)
    {
        var identityJson = System.Text.Json.JsonSerializer.Serialize(
            new { runnerId = Guid.NewGuid(), createdAt = DateTimeOffset.UtcNow, token },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        await File.WriteAllTextAsync(Path.Combine(stateDir, "identity.json"), identityJson, ct);
    }
}

