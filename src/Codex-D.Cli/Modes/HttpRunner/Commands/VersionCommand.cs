using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.State;
using CodexD.Shared.Output;
using CodexD.Shared.Paths;
using CodexD.Shared.Strings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands;

public sealed class VersionCommand : AsyncCommand<VersionCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--resolved-only")]
        [Description("Only query the runner that would be selected by discovery.")]
        public bool ResolvedOnly { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        OutputFormat format;
        try
        {
            format = settings.ResolveOutputFormat(OutputFormatUsage.Single);
        }
        catch (ArgumentException ex)
        {
            if (settings.Json || !string.IsNullOrWhiteSpace(settings.OutputFormat))
            {
                CliOutput.WriteJsonError("invalid_outputformat", ex.Message);
                return 2;
            }

            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var cwd = string.IsNullOrWhiteSpace(settings.Cd) ? Directory.GetCurrentDirectory() : settings.Cd!;
        cwd = PathPolicy.TrimTrailingSeparators(Path.GetFullPath(cwd));

        var cliAssembly = typeof(VersionCommand).Assembly;
        var cliVersion = GetBestVersionString(cliAssembly);
        var cliAssemblyVersion = cliAssembly.GetName().Version?.ToString();
        var cliInformational = GetInformationalVersion(cliAssembly);

        var explicitUrl =
            StringHelpers.TrimOrNull(settings.Url) ??
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_URL")) ??
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_RUNNER_URL"));

        var explicitToken =
            StringHelpers.TrimOrNull(settings.Token) ??
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_TOKEN")) ??
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_RUNNER_TOKEN"));

        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            var server = await ProbeServerAsync("explicit", explicitUrl, explicitToken, cwd, ct: cancellationToken);
            WriteOutput(format, cwd, cliVersion, cliAssemblyVersion, cliInformational, resolved: server, daemon: null, foreground: null);
            return server.Health == RunnerHealthStatus.Ok ? 0 : 1;
        }

        var isDev = BuildMode.IsDev();

        ProbeResult? daemon = await ProbeDaemonAsync(isDev, explicitToken, cancellationToken);
        ProbeResult? foreground = await ProbeForegroundAsync(isDev, cwd, explicitToken, cancellationToken);

        var resolved = daemon is not null && daemon.Health == RunnerHealthStatus.Ok
            ? daemon
            : foreground is not null && foreground.Health == RunnerHealthStatus.Ok
                ? foreground
                : null;

        if (settings.ResolvedOnly && resolved is not null)
        {
            if (resolved.Kind == "daemon")
            {
                foreground = null;
            }
            else if (resolved.Kind == "foreground")
            {
                daemon = null;
            }
        }

        WriteOutput(format, cwd, cliVersion, cliAssemblyVersion, cliInformational, resolved, daemon, foreground);
        return resolved is null ? 1 : 0;
    }

    private static void WriteOutput(
        OutputFormat format,
        string cwd,
        string cliVersion,
        string? cliAssemblyVersion,
        string? cliInformationalVersion,
        ProbeResult? resolved,
        ProbeResult? daemon,
        ProbeResult? foreground)
    {
        if (format != OutputFormat.Human)
        {
            CliOutput.WriteJsonLine(new
            {
                cwd,
                cli = new { version = cliVersion, assemblyVersion = cliAssemblyVersion, informationalVersion = cliInformationalVersion },
                resolved = resolved is null ? null : new { resolved.Kind, resolved.BaseUrl, resolved.Version, resolved.InformationalVersion, resolved.RunnerId },
                daemon,
                foreground
            });
            return;
        }

        AnsiConsole.Write(new Rule("[bold]codex-d http version[/]").LeftJustified());
        AnsiConsole.MarkupLine($"CLI: [grey]{Markup.Escape(cliVersion)}[/]");

        if (resolved is not null)
        {
            AnsiConsole.MarkupLine($"Resolved: [cyan]{Markup.Escape(resolved.Kind)}[/]  [grey]{Markup.Escape(resolved.BaseUrl ?? string.Empty)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("Resolved: [red]none[/]");
        }

        if (daemon is not null)
        {
            PrintProbe(daemon);
        }

        if (foreground is not null)
        {
            PrintProbe(foreground);
        }

        AnsiConsole.WriteLine();
    }

    private static void PrintProbe(ProbeResult probe)
    {
        var label = probe.Kind;
        var health = probe.Health switch
        {
            RunnerHealthStatus.Ok => "[green]ok[/]",
            RunnerHealthStatus.Unauthorized => "[yellow]unauthorized[/]",
            _ => "[red]unreachable[/]"
        };

        var version = probe.InformationalVersion ?? probe.Version;
        var versionText = string.IsNullOrWhiteSpace(version) ? "unknown" : version;

        var baseUrl = string.IsNullOrWhiteSpace(probe.BaseUrl) ? string.Empty : probe.BaseUrl!.TrimEnd('/');

        AnsiConsole.MarkupLine($"{label}: {health}  [grey]{Markup.Escape(baseUrl)}[/]  v[grey]{Markup.Escape(versionText)}[/]");
    }

    private static async Task<ProbeResult> ProbeDaemonAsync(bool isDev, string? explicitToken, CancellationToken ct)
    {
        var daemonStateDir =
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR")) ??
            StatePaths.GetDefaultDaemonStateDir(isDev);

        var daemonRuntimePath = Path.Combine(daemonStateDir, "daemon.runtime.json");
        if (!DaemonRuntimeFile.TryRead(daemonRuntimePath, out var runtime) || runtime is null)
        {
            return new ProbeResult("daemon", BaseUrl: null, Health: RunnerHealthStatus.Unreachable, Version: null, InformationalVersion: null, RunnerId: null, RuntimePath: daemonRuntimePath, Skipped: false);
        }

        var token = explicitToken ?? IdentityFileReader.TryReadToken(StatePaths.IdentityFile(daemonStateDir));
        var probed = await ProbeServerAsync("daemon", runtime.BaseUrl, token, cwd: null, ct: ct);
        return probed with { RuntimePath = daemonRuntimePath };
    }

    private static async Task<ProbeResult> ProbeForegroundAsync(bool isDev, string cwd, string? explicitToken, CancellationToken ct)
    {
        var foregroundPort = TryGetEnvInt("CODEX_D_FOREGROUND_PORT") ?? StatePaths.GetDefaultForegroundPort(isDev);
        var baseUrl = $"http://127.0.0.1:{foregroundPort}";

        var token = explicitToken;
        var health = await RunnerHealth.CheckAsync(baseUrl, token, ct);
        if (health == RunnerHealthStatus.Unauthorized && token is null)
        {
            var fgStateDir = StatePaths.GetForegroundStateDir(cwd);
            var tokenFromIdentity = IdentityFileReader.TryReadToken(StatePaths.IdentityFile(fgStateDir));
            if (!string.IsNullOrWhiteSpace(tokenFromIdentity))
            {
                token = tokenFromIdentity;
            }
        }

        return await ProbeServerAsync("foreground", baseUrl, token, cwd, ct: ct);
    }

    private static async Task<ProbeResult> ProbeServerAsync(
        string kind,
        string baseUrl,
        string? token,
        string? cwd,
        CancellationToken ct)
    {
        var health = await RunnerHealth.CheckAsync(baseUrl, token, ct);
        if (health != RunnerHealthStatus.Ok)
        {
            return new ProbeResult(kind, baseUrl, health, Version: null, InformationalVersion: null, RunnerId: null, RuntimePath: null, Skipped: false);
        }

        try
        {
            using var client = new RunnerClient(baseUrl, token);
            var info = await client.GetInfoAsync(ct);

            var version = info.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var informational = info.TryGetProperty("informationalVersion", out var iv) && iv.ValueKind == JsonValueKind.String ? iv.GetString() : null;
            var runnerId = info.TryGetProperty("runnerId", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() : null;

            return new ProbeResult(kind, baseUrl, health, version, informational, runnerId, RuntimePath: null, Skipped: false);
        }
        catch
        {
            return new ProbeResult(kind, baseUrl, RunnerHealthStatus.Unreachable, Version: null, InformationalVersion: null, RunnerId: null, RuntimePath: null, Skipped: false);
        }
    }

    private static int? TryGetEnvInt(string name)
    {
        var raw = StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable(name));
        return int.TryParse(raw, out var i) ? i : null;
    }

    private static string GetBestVersionString(Assembly assembly)
        => GetInformationalVersion(assembly) ?? assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static string? GetInformationalVersion(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private sealed record ProbeResult(
        string Kind,
        string? BaseUrl,
        RunnerHealthStatus Health,
        string? Version,
        string? InformationalVersion,
        string? RunnerId,
        string? RuntimePath,
        bool Skipped = false);
}
