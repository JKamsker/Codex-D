using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.Runs;
using CodexD.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Runs;

public sealed class MessagesCommand : AsyncCommand<MessagesCommand.Settings>
{
    public sealed class Settings : RunLogSettingsBase
    {
        [CommandOption("-n|--count <N>")]
        public int Count { get; init; } = 1;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Count <= 0)
        {
            AnsiConsole.MarkupLine("[red]--count must be > 0[/]");
            return 1;
        }

        string eventsFile;
        try
        {
            eventsFile = await RunLogResolver.ResolveEventsFilePathAsync(
                settings.FilePath,
                settings.StateDir,
                settings.RunId,
                BuildMode.IsDev(),
                cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to resolve events file:[/] {ex.Message}");
            return 1;
        }

        if (!File.Exists(eventsFile))
        {
            AnsiConsole.MarkupLine($"[red]events.jsonl not found:[/] {eventsFile}");
            return 1;
        }

        var lines = await RunEventsJsonl.ReadLinesAsync(eventsFile, settings.TailLines, cancellationToken);

        var queue = new Queue<string>(Math.Min(settings.Count, 128));
        foreach (var line in lines)
        {
            if (!RunEventsJsonl.TryGetCompletedAgentMessageText(line, out var text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (queue.Count == settings.Count)
            {
                queue.Dequeue();
            }
            queue.Enqueue(text);
        }

        if (queue.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No completed agent messages found in:[/] {eventsFile}");
            return 0;
        }

        var i = 0;
        foreach (var msg in queue)
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
}
