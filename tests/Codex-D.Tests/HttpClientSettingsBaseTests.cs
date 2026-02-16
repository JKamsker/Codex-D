using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.State;
using CodexD.Shared.Output;
using CodexD.Shared.Paths;
using Xunit;

namespace CodexD.Tests;

public sealed class ClientSettingsBaseTests
{
    private sealed class Settings : ClientSettingsBase
    {
    }

    [Fact]
    public void ResolveOutputFormat_Default_IsHuman()
    {
        var s = new Settings();
        Assert.Equal(OutputFormat.Human, s.ResolveOutputFormat(OutputFormatUsage.Single));
    }

    [Fact]
    public void ResolveOutputFormat_JsonFlag_Single_IsJson()
    {
        var s = new Settings { Json = true };
        Assert.Equal(OutputFormat.Json, s.ResolveOutputFormat(OutputFormatUsage.Single));
    }

    [Fact]
    public void ResolveOutputFormat_JsonFlag_Streaming_IsJsonl()
    {
        var s = new Settings { Json = true };
        Assert.Equal(OutputFormat.Jsonl, s.ResolveOutputFormat(OutputFormatUsage.Streaming));
    }

    [Fact]
    public void StatePaths_DefaultForegroundPort_Is8787()
    {
        Assert.Equal(8787, StatePaths.GetDefaultForegroundPort(isDev: false));
    }

    [Fact]
    public void StatePaths_DefaultForegroundPortDev_Is8788()
    {
        Assert.Equal(8788, StatePaths.GetDefaultForegroundPort(isDev: true));
    }

    [Fact]
    public void StatePaths_DefaultDaemonDevDir_EndsWithDaemonDev()
    {
        var dir = StatePaths.GetDefaultDaemonStateDir(isDev: true);
        Assert.EndsWith(Path.Combine("codex-d", "daemon-dev"), dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_UrlArg_WinsOverDaemonPreference()
    {
        var s = new Settings
        {
            Url = "http://127.0.0.1:1234/",
            Token = "arg-token",
            Cd = Path.Combine(Path.GetTempPath(), "repo") + Path.DirectorySeparatorChar
        };

        Directory.CreateDirectory(s.Cd);

        var resolved = await s.ResolveAsync(CancellationToken.None);
        Assert.Equal("http://127.0.0.1:1234/", resolved.BaseUrl);
        Assert.Equal("arg-token", resolved.Token);
        Assert.Equal(PathPolicy.TrimTrailingSeparators(Path.GetFullPath(s.Cd)), resolved.Cwd);
    }

    [Fact]
    public async Task ResolveAsync_PrefersExplicitArgs_OverEnv()
    {
        var oldUrl = Environment.GetEnvironmentVariable("CODEX_D_URL");
        var oldToken = Environment.GetEnvironmentVariable("CODEX_D_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_D_URL", "http://127.0.0.1:9999");
            Environment.SetEnvironmentVariable("CODEX_D_TOKEN", "env-token");

            var s = new Settings
            {
                Url = "http://127.0.0.1:1234/",
                Token = "arg-token",
                Cd = Path.Combine(Path.GetTempPath(), "repo") + Path.DirectorySeparatorChar
            };

            Directory.CreateDirectory(s.Cd);

            var resolved = await s.ResolveAsync(CancellationToken.None);
            Assert.Equal("http://127.0.0.1:1234/", resolved.BaseUrl);
            Assert.Equal("arg-token", resolved.Token);
            Assert.Equal(PathPolicy.TrimTrailingSeparators(Path.GetFullPath(s.Cd)), resolved.Cwd);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_D_URL", oldUrl);
            Environment.SetEnvironmentVariable("CODEX_D_TOKEN", oldToken);
        }
    }

    [Fact]
    public async Task ResolveAsync_SelectsDaemon_WhenRuntimePresentAndHealthy()
    {
        var oldDaemonStateDir = Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR");
        try
        {
            await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: true, new ImmediateSuccessExecutor());

            var daemonStateDir = RunnerResolutionTestUtils.CreateTempDir("codex-d-daemon-tests");

            Environment.SetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR", daemonStateDir);

            await RunnerResolutionTestUtils.WriteDaemonRuntimeAsync(daemonStateDir, host.BaseUrl, host.Port);
            await RunnerResolutionTestUtils.WriteIdentityAsync(daemonStateDir, host.Identity.Token);

            var s = new Settings();
            var resolved = await s.ResolveAsync(CancellationToken.None);

            Assert.Equal(host.BaseUrl, resolved.BaseUrl);
            Assert.Equal(host.Identity.Token, resolved.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR", oldDaemonStateDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToForeground_WhenDaemonMissing()
    {
        var oldDaemonStateDir = Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR");
        var oldFgPort = Environment.GetEnvironmentVariable("CODEX_D_FOREGROUND_PORT");
        try
        {
            var port = GetFreeTcpPort();
            await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor(), portOverride: port);

            var daemonStateDir = RunnerResolutionTestUtils.CreateTempDir("codex-d-daemon-tests");

            Environment.SetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR", daemonStateDir);
            Environment.SetEnvironmentVariable("CODEX_D_FOREGROUND_PORT", port.ToString());

            var s = new Settings();
            var resolved = await s.ResolveAsync(CancellationToken.None);

            Assert.Equal($"http://127.0.0.1:{port}", resolved.BaseUrl);
            Assert.Null(resolved.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR", oldDaemonStateDir);
            Environment.SetEnvironmentVariable("CODEX_D_FOREGROUND_PORT", oldFgPort);
        }
    }

    [Fact]
    public async Task ResolveAsync_ThrowsConsistentMessage_WhenNoServerAvailable()
    {
        var oldDaemonStateDir = Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR");
        var oldFgPort = Environment.GetEnvironmentVariable("CODEX_D_FOREGROUND_PORT");
        try
        {
            var daemonStateDir = RunnerResolutionTestUtils.CreateTempDir("codex-d-daemon-tests");

            var unusedPort = GetFreeTcpPort();
            Environment.SetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR", daemonStateDir);
            Environment.SetEnvironmentVariable("CODEX_D_FOREGROUND_PORT", unusedPort.ToString());

            var s = new Settings();

            var ex = await Assert.ThrowsAsync<RunnerResolutionFailure>(() => s.ResolveAsync(CancellationToken.None));
            Assert.Contains("No running codex-d HTTP server found.", ex.UserMessage, StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains("Start the daemon:", ex.UserMessage, StringComparison.Ordinal);
                Assert.Contains("codex-d http serve -d", ex.UserMessage, StringComparison.Ordinal);
            }
            Assert.Contains($"http://127.0.0.1:{unusedPort}", ex.UserMessage, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR", oldDaemonStateDir);
            Environment.SetEnvironmentVariable("CODEX_D_FOREGROUND_PORT", oldFgPort);
        }
    }

    [Fact]
    public async Task ResolveAsync_Foreground401_LoadsTokenFromProjectIdentity()
    {
        var oldDaemonStateDir = Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR");
        var oldFgPort = Environment.GetEnvironmentVariable("CODEX_D_FOREGROUND_PORT");
        try
        {
            var port = GetFreeTcpPort();
            await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: true, new ImmediateSuccessExecutor(), portOverride: port);

            var daemonStateDir = RunnerResolutionTestUtils.CreateTempDir("codex-d-daemon-tests");
            Environment.SetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR", daemonStateDir);
            Environment.SetEnvironmentVariable("CODEX_D_FOREGROUND_PORT", port.ToString());

            var repoDir = RunnerResolutionTestUtils.CreateTempDir("codex-d-repo-tests");
            var stateDir = Path.Combine(repoDir, ".codex-d");
            Directory.CreateDirectory(stateDir);
            await RunnerResolutionTestUtils.WriteIdentityAsync(stateDir, host.Identity.Token);

            var s = new Settings { Cd = repoDir };
            var resolved = await s.ResolveAsync(CancellationToken.None);

            Assert.Equal($"http://127.0.0.1:{port}", resolved.BaseUrl);
            Assert.Equal(host.Identity.Token, resolved.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR", oldDaemonStateDir);
            Environment.SetEnvironmentVariable("CODEX_D_FOREGROUND_PORT", oldFgPort);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
