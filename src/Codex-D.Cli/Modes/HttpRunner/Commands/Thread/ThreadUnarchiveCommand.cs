using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Thread;

public sealed class ThreadUnarchiveCommand : AsyncCommand<ThreadUnarchiveCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandArgument(0, "<THREAD_ID>")]
        [Description("Thread id.")]
        public string ThreadId { get; init; } = string.Empty;
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

        if (string.IsNullOrWhiteSpace(settings.ThreadId))
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("invalid_thread_id", "Missing THREAD_ID.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Missing THREAD_ID.[/]");
            }
            return 2;
        }

        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        try
        {
            var response = await client.UnarchiveThreadAsync(settings.ThreadId, cancellationToken);

            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonLine(response);
            }
            else
            {
                var id = string.IsNullOrWhiteSpace(response.ThreadId) ? settings.ThreadId.Trim() : response.ThreadId;
                AnsiConsole.MarkupLine($"Unarchived: [cyan]{Markup.Escape(id)}[/]");
            }
        }
        catch (Exception ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("thread_unarchive_failed", ex.Message ?? string.Empty);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to unarchive thread:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            }
            return 1;
        }

        return 0;
    }
}
