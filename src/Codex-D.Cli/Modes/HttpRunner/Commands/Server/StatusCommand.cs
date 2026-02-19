using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.State;
using CodexD.Shared.Output;
using CodexD.Shared.Paths;
using CodexD.Shared.Strings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Server;

public sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--port <PORT>")]
        [Description("TCP port (default: CODEX_D_FOREGROUND_PORT or 8787).")]
        public int? Port { get; init; }

        [CommandOption("--state-dir <DIR>")]
        [Description("Override the foreground state directory (identity + runs). Default: <cwd>/.codex-d.")]
        public string? StateDir { get; init; }

        [CommandOption("--token <TOKEN>")]
        [Description("Bearer token override (otherwise read from foreground identity.json if present).")]
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
        var port = settings.Port ?? TryGetEnvInt("CODEX_D_FOREGROUND_PORT") ?? StatePaths.GetDefaultForegroundPort(isDev);

        var baseUrl = $"http://127.0.0.1:{port}";

        var stateDirRaw =
            StringHelpers.TrimOrNull(settings.StateDir) ??
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_FOREGROUND_STATE_DIR")) ??
            StatePaths.GetForegroundStateDir(Directory.GetCurrentDirectory());
        var stateDir = Path.GetFullPath(stateDirRaw);

        var token =
            StringHelpers.TrimOrNull(settings.Token) ??
            IdentityFileReader.TryReadToken(StatePaths.IdentityFile(stateDir));

        var health = await RunnerHealth.CheckAsync(baseUrl, token, cancellationToken);

        if (json)
        {
            CliOutput.WriteJsonLine(new { baseUrl, port, stateDir, health = health.ToString().ToLowerInvariant() });
        }
        else
        {
            AnsiConsole.Write(new Rule("[bold]codex-d server status[/]").LeftJustified());
            AnsiConsole.MarkupLine($"Base URL: [cyan]{Markup.Escape(baseUrl)}[/]");
            AnsiConsole.MarkupLine($"Health: [grey]{health.ToString().ToLowerInvariant()}[/]");
            AnsiConsole.MarkupLine($"StateDir: [grey]{Markup.Escape(stateDir)}[/]");
            AnsiConsole.WriteLine();
        }

        return health == RunnerHealthStatus.Ok ? 0 : 1;
    }

    private static int? TryGetEnvInt(string name)
    {
        var raw = StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable(name));
        return int.TryParse(raw, out var i) ? i : null;
    }
}

