using System.ComponentModel;
using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.State;
using CodexD.Shared.Paths;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Client;

public abstract class ClientSettingsBase : CommandSettings
{
    [CommandOption("--url <URL>")]
    [Description("Runner base URL. Default: env CODEX_D_URL/CODEX_RUNNER_URL or daemon-first discovery.")]
    public string? Url { get; init; }

    [CommandOption("--token <TOKEN>")]
    [Description("Bearer token. Default: env CODEX_D_TOKEN/CODEX_RUNNER_TOKEN")]
    public string? Token { get; init; }

    [CommandOption("--cd <DIR>")]
    [Description("Working directory (exact-match filtering for ls/--last). Default: current directory")]
    public string? Cd { get; init; }

    [CommandOption("--json")]
    [Description("Print JSONL events (client-side envelope) instead of human-friendly output.")]
    public bool Json { get; init; }

    public async Task<ResolvedClientSettings> ResolveAsync(CancellationToken ct)
    {
        var isDev = BuildMode.IsDev();

        var cwd = string.IsNullOrWhiteSpace(Cd) ? Directory.GetCurrentDirectory() : Cd!;
        cwd = PathPolicy.TrimTrailingSeparators(Path.GetFullPath(cwd));

        var explicitUrl =
            TrimOrNull(Url) ??
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_URL")) ??
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_RUNNER_URL"));

        var explicitToken =
            TrimOrNull(Token) ??
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_TOKEN")) ??
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_RUNNER_TOKEN"));

        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return new ResolvedClientSettings { BaseUrl = explicitUrl, Token = explicitToken, Cwd = cwd };
        }

        var daemonStateDir =
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR")) ??
            StatePaths.GetDefaultDaemonStateDir(isDev);

        var daemonRuntimePath = Path.Combine(daemonStateDir, "daemon.runtime.json");
        var daemonAttempt = "missing";

        if (DaemonRuntimeFile.TryRead(daemonRuntimePath, out var runtime) && runtime is not null)
        {
            var daemonToken = explicitToken ?? IdentityFileReader.TryReadToken(StatePaths.IdentityFile(daemonStateDir));
            var health = await RunnerHealth.CheckAsync(runtime.BaseUrl, daemonToken, ct);
            if (health == RunnerHealthStatus.Ok)
            {
                return new ResolvedClientSettings { BaseUrl = runtime.BaseUrl, Token = daemonToken, Cwd = cwd };
            }

            daemonAttempt = health == RunnerHealthStatus.Unauthorized ? "unauthorized" : "unreachable";
        }
        else if (File.Exists(daemonRuntimePath))
        {
            daemonAttempt = "invalid";
        }

        var foregroundPort = TryGetEnvInt("CODEX_D_FOREGROUND_PORT") ?? StatePaths.GetDefaultForegroundPort(isDev);
        var foregroundBaseUrl = $"http://127.0.0.1:{foregroundPort}";

        var foregroundToken = explicitToken;
        var fgHealth = await RunnerHealth.CheckAsync(foregroundBaseUrl, foregroundToken, ct);

        if (fgHealth == RunnerHealthStatus.Ok)
        {
            return new ResolvedClientSettings { BaseUrl = foregroundBaseUrl, Token = foregroundToken, Cwd = cwd };
        }

        if (fgHealth == RunnerHealthStatus.Unauthorized && foregroundToken is null)
        {
            var fgStateDir = StatePaths.GetForegroundStateDir(cwd);
            var tokenFromIdentity = IdentityFileReader.TryReadToken(StatePaths.IdentityFile(fgStateDir));
            if (!string.IsNullOrWhiteSpace(tokenFromIdentity))
            {
                var retry = await RunnerHealth.CheckAsync(foregroundBaseUrl, tokenFromIdentity, ct);
                if (retry == RunnerHealthStatus.Ok)
                {
                    return new ResolvedClientSettings { BaseUrl = foregroundBaseUrl, Token = tokenFromIdentity, Cwd = cwd };
                }
            }
        }

        var fgAttempt = fgHealth == RunnerHealthStatus.Unauthorized ? "unauthorized" : "unreachable";

        var startHint = OperatingSystem.IsWindows()
            ?
$@"
Start the daemon:
  codex-d http serve -d
"
            : string.Empty;

        throw new RunnerResolutionFailure(
$@"No running codex-d HTTP server found.

Tried:
  - Daemon: {daemonRuntimePath} ({daemonAttempt})
  - Foreground: {foregroundBaseUrl} ({fgAttempt})
{startHint}
Or start a foreground server (project-local):
  codex-d http serve");
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? TryGetEnvInt(string name)
    {
        var raw = TrimOrNull(Environment.GetEnvironmentVariable(name));
        return int.TryParse(raw, out var i) ? i : null;
    }
}
