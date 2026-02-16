using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Run;

public sealed class InterruptCommand : AsyncCommand<InterruptCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--last")]
        [Description("Interrupt the most recent run for the current --cd/cwd.")]
        public bool Last { get; init; }

        [CommandArgument(0, "[RUN_ID]")]
        public string? RunId { get; init; }
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

        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        Guid runId;
        if (settings.Last)
        {
            var runs = await client.ListRunsAsync(resolved.Cwd, all: false, cancellationToken);
            var latest = runs.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (latest is null)
            {
                if (format != OutputFormat.Human)
                {
                    CliOutput.WriteJsonError("no_runs", "No runs found for this directory.");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]No runs found for this directory.[/]");
                }
                return 1;
            }

            runId = latest.RunId;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.RunId) || !Guid.TryParse(settings.RunId, out runId))
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
        }

        try
        {
            await client.InterruptAsync(runId, cancellationToken);
        }
        catch (Exception ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("interrupt_failed", ex.Message ?? string.Empty);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to interrupt run:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            }
            return 1;
        }

        if (format != OutputFormat.Human)
        {
            CliOutput.WriteJsonLine(new { eventName = "run.interrupted", runId });
        }
        else
        {
            AnsiConsole.MarkupLine($"Interrupted: [cyan]{runId:D}[/]");
        }

        return 0;
    }
}
