using System.ComponentModel;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using CodexD.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Run;

public sealed class RunMessagesCommand : AsyncCommand<RunMessagesCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("-n|--count <N>")]
        [Description("Number of messages to print (last N).")]
        public int Count { get; init; } = 1;

        [CommandOption("--tail-events <N>")]
        [Description("Only scan the last N events on the server (performance guard). Default: 20000.")]
        public int? TailEvents { get; init; }

        [CommandOption("--last")]
        [Description("Use the most recent run for the current --cd/cwd.")]
        public bool Last { get; init; }

        [CommandArgument(0, "[RUN_ID]")]
        public string? RunId { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Count <= 0)
        {
            AnsiConsole.MarkupLine("[red]--count must be > 0[/]");
            return 2;
        }

        ResolvedClientSettings resolved;
        try
        {
            resolved = await settings.ResolveAsync(cancellationToken);
        }
        catch (RunnerResolutionFailure ex)
        {
            Console.Error.WriteLine(ex.UserMessage);
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
                AnsiConsole.MarkupLine("[red]No runs found for this directory.[/]");
                return 1;
            }

            runId = latest.RunId;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.RunId) || !Guid.TryParse(settings.RunId, out runId))
            {
                AnsiConsole.MarkupLine("[red]Missing or invalid RUN_ID.[/]");
                return 2;
            }
        }

        IReadOnlyList<string> messages;
        try
        {
            messages = await client.GetRunMessagesAsync(runId, settings.Count, settings.TailEvents, cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to fetch messages:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            return 1;
        }

        if (settings.Json)
        {
            WriteJson(new { runId, items = messages });
            return 0;
        }

        if (messages.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No completed agent messages found for:[/] {runId:D}");
            return 0;
        }

        var i = 0;
        foreach (var msg in messages)
        {
            if (i++ > 0)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("-----");
                Console.Out.WriteLine();
            }

            Console.Out.WriteLine(TextMojibakeRepair.Fix(msg));
        }

        return 0;
    }

    private static void WriteJson(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Console.Out.WriteLine(json);
    }
}
