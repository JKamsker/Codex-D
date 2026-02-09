using CodexWebUi.Runner.HttpRunner.Client;
using CodexWebUi.Runner.Shared.Paths;
using Xunit;

namespace CodexWebUi.Runner.Tests;

public sealed class ClientSettingsBaseTests
{
    private sealed class Settings : ClientSettingsBase
    {
    }

    [Fact]
    public void Resolve_UsesHardcodedDefaults_WhenNoArgsOrEnv()
    {
        var oldUrl = Environment.GetEnvironmentVariable("CODEX_RUNNER_URL");
        var oldToken = Environment.GetEnvironmentVariable("CODEX_RUNNER_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_RUNNER_URL", null);
            Environment.SetEnvironmentVariable("CODEX_RUNNER_TOKEN", null);

            var s = new Settings();
            var resolved = s.Resolve();

            Assert.Equal("http://127.0.0.1:8787", resolved.BaseUrl);
            Assert.Null(resolved.Token);
            Assert.Equal(PathPolicy.TrimTrailingSeparators(Path.GetFullPath(Directory.GetCurrentDirectory())), resolved.Cwd);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_RUNNER_URL", oldUrl);
            Environment.SetEnvironmentVariable("CODEX_RUNNER_TOKEN", oldToken);
        }
    }

    [Fact]
    public void Resolve_PrefersExplicitArgs_OverEnv()
    {
        var oldUrl = Environment.GetEnvironmentVariable("CODEX_RUNNER_URL");
        var oldToken = Environment.GetEnvironmentVariable("CODEX_RUNNER_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_RUNNER_URL", "http://127.0.0.1:9999");
            Environment.SetEnvironmentVariable("CODEX_RUNNER_TOKEN", "env-token");

            var s = new Settings
            {
                Url = "http://127.0.0.1:1234/",
                Token = "arg-token",
                Cd = Path.Combine(Path.GetTempPath(), "repo") + Path.DirectorySeparatorChar
            };

            Directory.CreateDirectory(s.Cd);

            var resolved = s.Resolve();
            Assert.Equal("http://127.0.0.1:1234/", resolved.BaseUrl);
            Assert.Equal("arg-token", resolved.Token);
            Assert.Equal(PathPolicy.TrimTrailingSeparators(Path.GetFullPath(s.Cd)), resolved.Cwd);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_RUNNER_URL", oldUrl);
            Environment.SetEnvironmentVariable("CODEX_RUNNER_TOKEN", oldToken);
        }
    }
}
