using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Run;

public sealed class StopCommand : AsyncCommand<StopCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandArgument(0, "<RUN_ID>")]
        [Description("Run id (GUID).")]
        public string RunId { get; init; } = string.Empty;
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

        ResolvedClientSettings resolved;
        try
        {
            resolved = await settings.ResolveAsync(cancellationToken);
        }
        catch (RunnerResolutionFailure ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("runner_not_found", ex.UserMessage);
            }
            else
            {
                Console.Error.WriteLine(ex.UserMessage);
            }
            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.RunId) || !Guid.TryParse(settings.RunId, out var runId))
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("invalid_run_id", "Missing or invalid RUN_ID.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Missing or invalid RUN_ID.[/]");
            }
            return 2;
        }

        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        try
        {
            await client.StopAsync(runId, cancellationToken);
        }
        catch (Exception ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("stop_failed", ex.Message ?? string.Empty);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to stop run:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            }
            return 1;
        }

        if (format != OutputFormat.Human)
        {
            CliOutput.WriteJsonLine(new { eventName = "run.stop_requested", runId });
        }
        else
        {
            AnsiConsole.MarkupLine($"Stop requested: [cyan]{runId:D}[/]");
        }

        return 0;
    }
}
