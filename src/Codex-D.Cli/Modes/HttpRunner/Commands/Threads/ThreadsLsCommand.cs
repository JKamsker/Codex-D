using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts.Threads;
using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Threads;

public sealed class ThreadsLsCommand : AsyncCommand<ThreadsLsCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--all")]
        [Description("List threads across all working directories (omit cwd filter).")]
        public bool All { get; init; }

        [CommandOption("--archived <BOOL>")]
        [Description("Filter: archived=true/false.")]
        public string? Archived { get; init; }

        [CommandOption("-q|--query <TERM>")]
        [Description("Substring search term (if supported upstream).")]
        public string? Query { get; init; }

        [CommandOption("--limit <N>")]
        [Description("Page size (if supported upstream).")]
        public int? Limit { get; init; }

        [CommandOption("--cursor <CURSOR>")]
        [Description("Paging cursor token.")]
        public string? Cursor { get; init; }

        [CommandOption("--sort-key <KEY>")]
        [Description("Sort key (e.g. created_at, updated_at).")]
        public string? SortKey { get; init; }
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

        bool? archived = null;
        if (!string.IsNullOrWhiteSpace(settings.Archived))
        {
            if (!bool.TryParse(settings.Archived.Trim(), out var b))
            {
                if (format != OutputFormat.Human)
                {
                    CliOutput.WriteJsonError("invalid_archived", "Invalid --archived value. Use true/false.");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Invalid --archived value. Use true/false.[/]");
                }
                return 2;
            }

            archived = b;
        }

        var cwd = settings.All ? null : resolved.Cwd;

        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        ThreadListResponse result;
        try
        {
            result = await client.ListThreadsAsync(
                archived: archived,
                cwd: cwd,
                query: settings.Query,
                limit: settings.Limit,
                cursor: settings.Cursor,
                sortKey: settings.SortKey,
                cancellationToken);
        }
        catch (Exception ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("threads_list_failed", ex.Message ?? string.Empty);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to list threads:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            }
            return 1;
        }

        if (format != OutputFormat.Human)
        {
            CliOutput.WriteJsonLine(result);
            return 0;
        }

        AnsiConsole.Write(new Rule("[bold]codex-d threads ls[/]").LeftJustified());
        if (!settings.All && !string.IsNullOrWhiteSpace(cwd))
        {
            AnsiConsole.MarkupLine($"Cwd: [grey]{Markup.Escape(cwd)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(result.NextCursor))
        {
            AnsiConsole.MarkupLine($"NextCursor: [grey]{Markup.Escape(result.NextCursor)}[/]");
        }

        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("ThreadId");
        table.AddColumn("Name");
        table.AddColumn("Archived");
        table.AddColumn("Status");
        table.AddColumn("Cwd");
        table.AddColumn("Model");
        table.AddColumn("Created");

        foreach (var t in result.Items)
        {
            var archivedText = t.Archived is true ? "yes" : t.Archived is false ? "no" : "-";

            var status = string.IsNullOrWhiteSpace(t.StatusType) ? "-" : t.StatusType.Trim();
            if (t.ActiveFlags is { Count: > 0 })
            {
                status = $"{status} ({string.Join(",", t.ActiveFlags)})";
            }

            var created = t.CreatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

            table.AddRow(
                t.ThreadId,
                string.IsNullOrWhiteSpace(t.Name) ? "-" : t.Name,
                archivedText,
                status,
                string.IsNullOrWhiteSpace(t.Cwd) ? "-" : t.Cwd,
                string.IsNullOrWhiteSpace(t.Model) ? "-" : t.Model,
                created);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
