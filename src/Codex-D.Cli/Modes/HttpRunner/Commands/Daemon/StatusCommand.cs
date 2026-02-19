using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.State;
using CodexD.Shared.Output;
using CodexD.Shared.Paths;
using CodexD.Shared.Strings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Daemon;

public sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--state-dir <DIR>")]
        [Description("Override the daemon state directory. Default: env CODEX_D_DAEMON_STATE_DIR or %LOCALAPPDATA%/codex-d/daemon/config")]
        public string? StateDir { get; init; }

        [CommandOption("--token <TOKEN>")]
        [Description("Bearer token override (otherwise read from daemon identity.json).")]
        public string? Token { get; init; }

        [CommandOption("--output-format|--outputformat <FORMAT>")]
        [Description("Output format: human, json, or jsonl. Default: human.")]
        public string? OutputFormat { get; init; }

        [CommandOption("--json")]
        [Description("Deprecated. Use --outputformat json/jsonl.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        OutputFormat format;
        try
        {
            format = OutputFormatParser.Resolve(settings.OutputFormat, settings.Json, OutputFormatUsage.Single);
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

        var json = format != OutputFormat.Human;
        var isDev = BuildMode.IsDev();

        var stateDirRaw =
            StringHelpers.TrimOrNull(settings.StateDir) ??
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR")) ??
            StatePaths.GetDefaultDaemonStateDir(isDev);
        var stateDir = Path.GetFullPath(stateDirRaw);

        var runtimePath = Path.Combine(stateDir, "daemon.runtime.json");
        if (!DaemonRuntimeFile.TryRead(runtimePath, out var runtime) || runtime is null)
        {
            if (json)
            {
                CliOutput.WriteJsonError("daemon_not_running", "No daemon runtime file found.", new { stateDir, runtimePath });
            }
            else
            {
                AnsiConsole.MarkupLine("[red]No daemon runtime file found.[/]");
                AnsiConsole.MarkupLine($"StateDir: [grey]{Markup.Escape(stateDir)}[/]");
            }
            return 1;
        }

        var token =
            StringHelpers.TrimOrNull(settings.Token) ??
            IdentityFileReader.TryReadToken(StatePaths.IdentityFile(stateDir));

        var health = await RunnerHealth.CheckAsync(runtime.BaseUrl, token, cancellationToken);

        if (json)
        {
            CliOutput.WriteJsonLine(new
            {
                daemon = new { runtime.BaseUrl, runtime.Port, runtime.Pid, runtime.StartedAtUtc, runtime.StateDir, runtime.Version },
                health = health.ToString().ToLowerInvariant()
            });
        }
        else
        {
            AnsiConsole.Write(new Rule("[bold]codex-d daemon status[/]").LeftJustified());
            AnsiConsole.MarkupLine($"Base URL: [cyan]{Markup.Escape(runtime.BaseUrl)}[/]");
            AnsiConsole.MarkupLine($"Health: [grey]{health.ToString().ToLowerInvariant()}[/]");
            AnsiConsole.MarkupLine($"Pid: [grey]{runtime.Pid}[/]");
            AnsiConsole.MarkupLine($"StateDir: [grey]{Markup.Escape(runtime.StateDir)}[/]");
            AnsiConsole.MarkupLine($"StartedAtUtc: [grey]{Markup.Escape(runtime.StartedAtUtc.ToString("O"))}[/]");
            AnsiConsole.MarkupLine($"Version: [grey]{Markup.Escape(runtime.Version)}[/]");
            AnsiConsole.WriteLine();
        }

        return health == RunnerHealthStatus.Ok ? 0 : 1;
    }
}
