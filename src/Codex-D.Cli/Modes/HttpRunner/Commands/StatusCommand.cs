using System.ComponentModel;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.State;
using CodexD.Shared.Output;
using CodexD.Shared.Paths;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands;

public sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--verbose")]
        [Description("Print all probe attempts even when a runner is reachable.")]
        public bool Verbose { get; init; }
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

        var isDev = BuildMode.IsDev();

        var cwd = string.IsNullOrWhiteSpace(settings.Cd) ? Directory.GetCurrentDirectory() : settings.Cd!;
        cwd = PathPolicy.TrimTrailingSeparators(Path.GetFullPath(cwd));

        var explicitUrl =
            TrimOrNull(settings.Url) ??
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_URL")) ??
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_RUNNER_URL"));

        var explicitToken =
            TrimOrNull(settings.Token) ??
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_TOKEN")) ??
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_RUNNER_TOKEN"));

        var probes = new List<object>();

        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            var health = await RunnerHealth.CheckAsync(explicitUrl, explicitToken, cancellationToken);
            probes.Add(new { kind = "explicit", baseUrl = explicitUrl, health = health.ToString().ToLowerInvariant() });

            if (format != OutputFormat.Human)
            {
                await WriteStatusJsonAsync(explicitUrl, explicitToken, probes, cancellationToken);
                return health == RunnerHealthStatus.Ok ? 0 : 1;
            }

            PrintProbeSummary(probes, verbose: true);
            if (health != RunnerHealthStatus.Ok)
            {
                return 1;
            }

            await PrintRunnerInfoAsync(explicitUrl, explicitToken, cancellationToken);
            return 0;
        }

        // Daemon probe
        var daemonStateDir =
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR")) ??
            StatePaths.GetDefaultDaemonStateDir(isDev);

        var daemonRuntimePath = Path.Combine(daemonStateDir, "daemon.runtime.json");
        var daemonAttempt = "missing";
        string? daemonBaseUrl = null;
        string? daemonToken = null;

        if (DaemonRuntimeFile.TryRead(daemonRuntimePath, out var runtime) && runtime is not null)
        {
            daemonBaseUrl = runtime.BaseUrl;
            daemonToken = explicitToken ?? IdentityFileReader.TryReadToken(StatePaths.IdentityFile(daemonStateDir));
            var health = await RunnerHealth.CheckAsync(daemonBaseUrl, daemonToken, cancellationToken);
            daemonAttempt = health == RunnerHealthStatus.Ok
                ? "ok"
                : health == RunnerHealthStatus.Unauthorized
                    ? "unauthorized"
                    : "unreachable";
        }
        else if (File.Exists(daemonRuntimePath))
        {
            daemonAttempt = "invalid";
        }

        probes.Add(new { kind = "daemon", runtimePath = daemonRuntimePath, baseUrl = daemonBaseUrl, health = daemonAttempt });

        // Foreground probe
        var foregroundPort = TryGetEnvInt("CODEX_D_FOREGROUND_PORT") ?? StatePaths.GetDefaultForegroundPort(isDev);
        var foregroundBaseUrl = $"http://127.0.0.1:{foregroundPort}";

        var fgToken = explicitToken;
        var fgHealth = await RunnerHealth.CheckAsync(foregroundBaseUrl, fgToken, cancellationToken);
        var fgAttempt = fgHealth == RunnerHealthStatus.Ok
            ? "ok"
            : fgHealth == RunnerHealthStatus.Unauthorized
                ? "unauthorized"
                : "unreachable";

        if (fgHealth == RunnerHealthStatus.Unauthorized && fgToken is null)
        {
            var fgStateDir = StatePaths.GetForegroundStateDir(cwd);
            var tokenFromIdentity = IdentityFileReader.TryReadToken(StatePaths.IdentityFile(fgStateDir));
            if (!string.IsNullOrWhiteSpace(tokenFromIdentity))
            {
                var retry = await RunnerHealth.CheckAsync(foregroundBaseUrl, tokenFromIdentity, cancellationToken);
                if (retry == RunnerHealthStatus.Ok)
                {
                    fgToken = tokenFromIdentity;
                    fgAttempt = "ok";
                }
            }
        }

        probes.Add(new { kind = "foreground", baseUrl = foregroundBaseUrl, health = fgAttempt });

        // Choose resolved target (same preference order as resolver)
        var resolvedBaseUrl = daemonAttempt == "ok" ? daemonBaseUrl : fgAttempt == "ok" ? foregroundBaseUrl : null;
        var resolvedToken = daemonAttempt == "ok" ? daemonToken : fgAttempt == "ok" ? fgToken : null;
        var resolvedSource = daemonAttempt == "ok" ? "daemon" : fgAttempt == "ok" ? "foreground" : null;

        if (format != OutputFormat.Human)
        {
            var payload = new
            {
                cwd,
                resolved = resolvedBaseUrl is null ? null : new { baseUrl = resolvedBaseUrl, source = resolvedSource, tokenPresent = resolvedToken is not null },
                probes
            };

            CliOutput.WriteJsonLine(payload);
            return resolvedBaseUrl is null ? 1 : 0;
        }

        PrintProbeSummary(probes, settings.Verbose || resolvedBaseUrl is null);

        if (resolvedBaseUrl is null)
        {
            PrintNoRunnerFoundHint();
            return 1;
        }

        await PrintRunnerInfoAsync(resolvedBaseUrl, resolvedToken, cancellationToken);
        return 0;
    }

    private static async Task WriteStatusJsonAsync(string baseUrl, string? token, List<object> probes, CancellationToken ct)
    {
        var payload = new { resolved = new { baseUrl, tokenPresent = token is not null }, probes };
        using var client = new RunnerClient(baseUrl, token);
        try
        {
            var health = await client.GetHealthAsync(ct);
            var info = await client.GetInfoAsync(ct);
            CliOutput.WriteJsonLine(new { payload.resolved, payload.probes, health, info });
        }
        catch
        {
            CliOutput.WriteJsonLine(payload);
        }
    }

    private static void PrintProbeSummary(List<object> probes, bool verbose)
    {
        if (!verbose)
        {
            return;
        }

        AnsiConsole.Write(new Rule("[bold]codex-d http status[/]").LeftJustified());
        foreach (var p in probes)
        {
            AnsiConsole.MarkupLine($"[grey]{JsonSerializer.Serialize(p, new JsonSerializerOptions(JsonSerializerDefaults.Web))}[/]");
        }
        AnsiConsole.WriteLine();
    }

    private static async Task PrintRunnerInfoAsync(string baseUrl, string? token, CancellationToken ct)
    {
        using var client = new RunnerClient(baseUrl, token);
        JsonElement health;
        JsonElement info;
        try
        {
            health = await client.GetHealthAsync(ct);
            info = await client.GetInfoAsync(ct);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to query runner:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            return;
        }

        var status = health.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var codexRuntime = health.TryGetProperty("codexRuntime", out var cr) && cr.ValueKind == JsonValueKind.String ? cr.GetString() : null;

        AnsiConsole.MarkupLine($"Base URL: [cyan]{baseUrl.TrimEnd('/')}[/]");
        AnsiConsole.MarkupLine($"Health: [grey]{status ?? "unknown"}[/]  CodexRuntime: [grey]{codexRuntime ?? "unknown"}[/]");

        if (info.TryGetProperty("runnerId", out var rid) && rid.ValueKind == JsonValueKind.String)
        {
            AnsiConsole.MarkupLine($"RunnerId: [cyan]{rid.GetString()}[/]");
        }
        if (info.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String)
        {
            AnsiConsole.MarkupLine($"Version: [grey]{ver.GetString()}[/]");
        }
        if (info.TryGetProperty("stateDir", out var sd) && sd.ValueKind == JsonValueKind.String)
        {
            AnsiConsole.MarkupLine($"StateDir: [grey]{sd.GetString()}[/]");
        }
        if (info.TryGetProperty("startedAtUtc", out var started) && started.ValueKind == JsonValueKind.String)
        {
            AnsiConsole.MarkupLine($"StartedAtUtc: [grey]{started.GetString()}[/]");
        }

        if (info.TryGetProperty("requireAuth", out var ra) && ra.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            AnsiConsole.MarkupLine($"Auth: [grey]{(ra.GetBoolean() ? "required" : "not required")}[/]");
        }

        AnsiConsole.WriteLine();
    }

    private static void PrintNoRunnerFoundHint()
    {
        AnsiConsole.MarkupLine("[red]No running codex-d HTTP server found.[/]");
        AnsiConsole.WriteLine();

        if (OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("Start the daemon:");
            AnsiConsole.MarkupLine("  [grey]codex-d http serve -d[/]");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("Or start a foreground server (project-local):");
        AnsiConsole.MarkupLine("  [grey]codex-d http serve[/]");
        AnsiConsole.WriteLine();
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? TryGetEnvInt(string name)
    {
        var raw = TrimOrNull(Environment.GetEnvironmentVariable(name));
        return int.TryParse(raw, out var i) ? i : null;
    }
}
