using System.ComponentModel;
using System.Net.Http.Headers;
using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.State;
using CodexD.Shared.Output;
using CodexD.Shared.Paths;
using CodexD.Shared.Strings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Server;

public sealed class StopCommand : AsyncCommand<StopCommand.Settings>
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

        var ok = await TryShutdownAsync(baseUrl, token, cancellationToken);
        if (!ok)
        {
            if (json)
            {
                CliOutput.WriteJsonError("shutdown_failed", "Failed to request shutdown.", new { baseUrl, port, stateDir });
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Failed to request shutdown.[/]");
                AnsiConsole.MarkupLine($"Base URL: [grey]{Markup.Escape(baseUrl)}[/]");
            }
            return 1;
        }

        if (json)
        {
            CliOutput.WriteJsonLine(new { eventName = "server.stop_requested", baseUrl, port });
        }
        else
        {
            AnsiConsole.MarkupLine("Server stop requested.");
        }

        return 0;
    }

    private static async Task<bool> TryShutdownAsync(string baseUrl, string? token, CancellationToken ct)
    {
        using var http = new HttpClient();
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        try
        {
            using var res = await http.PostAsync($"{baseUrl.TrimEnd('/')}/v1/shutdown", content: null, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static int? TryGetEnvInt(string name)
    {
        var raw = StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable(name));
        return int.TryParse(raw, out var i) ? i : null;
    }
}

