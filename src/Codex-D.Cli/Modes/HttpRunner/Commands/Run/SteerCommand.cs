using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Run;

public sealed class SteerCommand : AsyncCommand<SteerCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--last")]
        [Description("Steer the most recent run for the current --cd/cwd.")]
        public bool Last { get; init; }

        [CommandOption("-p|--prompt <PROMPT>")]
        [Description("Steer text to send to the active turn. Use '-' to read stdin.")]
        public string? PromptOption { get; init; }

        [CommandArgument(0, "[RUN_ID]")]
        public string? RunId { get; init; }

        [CommandArgument(1, "[PROMPT]")]
        [Description("Steer text to send to the active turn. Use '-' to read stdin.")]
        public string[] Prompt { get; init; } = [];
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
            if (settings.Json)
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

        var prompt = ResolvePrompt(settings);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("invalid_args", "Missing steer prompt.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Missing steer prompt.[/]");
            }
            return 2;
        }

        try
        {
            await client.SteerAsync(runId, prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("steer_failed", ex.Message ?? string.Empty);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to steer run:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            }
            return 1;
        }

        if (format != OutputFormat.Human)
        {
            CliOutput.WriteJsonLine(new { eventName = "run.steered", runId });
        }
        else
        {
            AnsiConsole.MarkupLine($"Steered: [cyan]{runId:D}[/]");
        }

        return 0;
    }

    private static string ResolvePrompt(Settings settings)
    {
        var prompt = settings.PromptOption;
        if (string.IsNullOrWhiteSpace(prompt) && settings.Prompt.Length > 0)
        {
            prompt = string.Join(" ", settings.Prompt);
        }

        if (string.Equals(prompt, "-", StringComparison.Ordinal))
        {
            return Console.In.ReadToEnd();
        }

        return string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt;
    }
}

