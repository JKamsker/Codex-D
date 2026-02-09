using System.ComponentModel;
using CodexD.HttpRunner.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands;

public sealed class AttachCommand : AsyncCommand<AttachCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--follow-only")]
        [Description("Do not replay history; only stream new events.")]
        public bool FollowOnly { get; init; }

        [CommandOption("--tail <N>")]
        [Description("Replay only the last N events, then follow.")]
        public int? Tail { get; init; }

        [CommandOption("--no-follow")]
        [Description("Replay (or tail) and then exit even if the run is still running.")]
        public bool NoFollow { get; init; }

        [CommandOption("--last")]
        [Description("Attach to the most recent run for the current --cd/cwd.")]
        public bool Last { get; init; }

        [CommandArgument(0, "[RUN_ID]")]
        public string? RunId { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var resolved = settings.Resolve();
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

        var replay = !settings.FollowOnly;
        var follow = !settings.NoFollow;
        var tail = settings.Tail;

        if (settings.FollowOnly && settings.Tail is not null)
        {
            AnsiConsole.MarkupLine("[red]--follow-only conflicts with --tail.[/]");
            return 2;
        }

        if (settings.NoFollow && settings.FollowOnly)
        {
            AnsiConsole.MarkupLine("[red]--no-follow conflicts with --follow-only.[/]");
            return 2;
        }

        if (!settings.Json)
        {
            AnsiConsole.MarkupLine($"Attaching to [cyan]{runId:D}[/]...");
        }

        return await ExecCommand.StreamAsync(client, runId, replay, follow, tail, settings.Json, cancellationToken);
    }
}
