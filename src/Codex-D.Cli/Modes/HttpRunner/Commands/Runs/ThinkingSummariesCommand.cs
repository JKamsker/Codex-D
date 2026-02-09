using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.Runs;
using CodexD.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Runs;

public sealed class ThinkingSummariesCommand : AsyncCommand<ThinkingSummariesCommand.Settings>
{
    public sealed class Settings : RunLogSettingsBase
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
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

        var summaries = new List<string>();
        var last = string.Empty;
        var inThinking = false;

        foreach (var line in lines)
        {
            if (!RunEventsJsonl.TryGetOutputDelta(line, out var delta) || string.IsNullOrWhiteSpace(delta))
            {
                continue;
            }

            delta = TextMojibakeRepair.Fix(delta);

            var trimmed = delta.Trim();
            if (string.Equals(trimmed, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                inThinking = true;
                continue;
            }
            if (string.Equals(trimmed, "final", StringComparison.OrdinalIgnoreCase))
            {
                inThinking = false;
                continue;
            }

            var maybeThinking = inThinking || delta.Contains("thinking", StringComparison.OrdinalIgnoreCase);
            if (!maybeThinking)
            {
                continue;
            }

            foreach (var rawLine in delta.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var t = rawLine.Trim();
                if (!t.StartsWith("**", StringComparison.Ordinal) || !t.EndsWith("**", StringComparison.Ordinal) || t.Length <= 4)
                {
                    continue;
                }

                var summary = TextMojibakeRepair.Fix(t[2..^2].Trim());
                if (string.IsNullOrWhiteSpace(summary))
                {
                    continue;
                }

                if (string.Equals(summary, last, StringComparison.Ordinal))
                {
                    continue;
                }

                summaries.Add(summary);
                last = summary;
            }
        }

        if (summaries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No thinking summaries found in:[/] {eventsFile}");
            return 0;
        }

        foreach (var s in summaries)
        {
            Console.Out.WriteLine(s);
        }

        return 0;
    }
}
