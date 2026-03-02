using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts.Threads;
using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Thread;

public sealed class ThreadReadCommand : AsyncCommand<ThreadReadCommand.Settings>
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

        ThreadReadResponse response;
        try
        {
            response = await client.ReadThreadAsync(settings.ThreadId, cancellationToken);
        }
        catch (Exception ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("thread_read_failed", ex.Message ?? string.Empty);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to read thread:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            }
            return 1;
        }

        if (format != OutputFormat.Human)
        {
            CliOutput.WriteJsonLine(response);
            return 0;
        }

        var t = response.Thread;

        AnsiConsole.Write(new Rule("[bold]codex-d thread read[/]").LeftJustified());
        AnsiConsole.MarkupLine($"ThreadId: [cyan]{Markup.Escape(t.ThreadId)}[/]");
        if (!string.IsNullOrWhiteSpace(t.Name))
        {
            AnsiConsole.MarkupLine($"Name: [grey]{Markup.Escape(t.Name)}[/]");
        }
        if (t.Archived is { } archived)
        {
            AnsiConsole.MarkupLine($"Archived: [grey]{(archived ? "yes" : "no")}[/]");
        }
        if (!string.IsNullOrWhiteSpace(t.StatusType))
        {
            var status = t.StatusType;
            if (t.ActiveFlags is { Count: > 0 })
            {
                status = $"{status} ({string.Join(",", t.ActiveFlags)})";
            }
            AnsiConsole.MarkupLine($"Status: [grey]{Markup.Escape(status)}[/]");
        }
        if (t.CreatedAt is { } createdAt)
        {
            AnsiConsole.MarkupLine($"Created: [grey]{createdAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}[/]");
        }
        if (!string.IsNullOrWhiteSpace(t.Cwd))
        {
            AnsiConsole.MarkupLine($"Cwd: [grey]{Markup.Escape(t.Cwd)}[/]");
        }
        if (!string.IsNullOrWhiteSpace(t.Model))
        {
            AnsiConsole.MarkupLine($"Model: [grey]{Markup.Escape(t.Model)}[/]");
        }

        return 0;
    }
}
