using System.ComponentModel;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Run;

public sealed class ResumeCommand : AsyncCommand<ResumeCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("-p|--prompt <PROMPT>")]
        [Description("Prompt text for the resumed turn. Default: \"continue\". Use '-' to read stdin.")]
        public string? PromptOption { get; init; }

        [CommandOption("-d|--detach")]
        [Description("Detach after resuming the run (does not stream output).")]
        public bool Detach { get; init; }

        [CommandOption("--follow-only")]
        [Description("Do not replay history; only stream new events.")]
        public bool FollowOnly { get; init; }

        [CommandOption("--tail <N>")]
        [Description("Replay only the last N events, then follow.")]
        public int? Tail { get; init; }

        [CommandOption("--no-follow")]
        [Description("Replay (or tail) and then exit even if the run is still running.")]
        public bool NoFollow { get; init; }

        [CommandArgument(0, "<RUN_ID>")]
        [Description("Run id (GUID).")]
        public string RunId { get; init; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
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

        if (string.IsNullOrWhiteSpace(settings.RunId) || !Guid.TryParse(settings.RunId, out var runId))
        {
            AnsiConsole.MarkupLine("[red]Missing or invalid RUN_ID.[/]");
            return 2;
        }

        var prompt = ResolvePrompt(settings);

        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        try
        {
            var resumed = await client.ResumeAsync(runId, prompt, cancellationToken);
            if (!settings.Json)
            {
                AnsiConsole.MarkupLine($"RunId: [cyan]{resumed.RunId:D}[/]  Status: [grey]{resumed.Status}[/]");
            }
            else
            {
                WriteJsonLine(new { eventName = "run.resumed", runId = resumed.RunId, status = resumed.Status });
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to resume run:[/] {ex.Message}");
            return 1;
        }

        if (settings.Detach)
        {
            return 0;
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

        return await ExecCommand.StreamAsync(client, runId, replay, follow, tail, settings.Json, cancellationToken);
    }

    private static string ResolvePrompt(Settings settings)
    {
        var prompt = settings.PromptOption;

        if (string.Equals(prompt, "-", StringComparison.Ordinal))
        {
            prompt = Console.In.ReadToEnd();
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = "continue";
        }

        return prompt;
    }

    private static void WriteJsonLine(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Console.Out.WriteLine(json);
    }
}

