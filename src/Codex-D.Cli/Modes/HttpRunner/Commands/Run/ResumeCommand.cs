using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Commands;
using CodexD.Shared.Output;
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

        [CommandOption("--effort|--reasoning-effort <EFFORT>")]
        [Description("Reasoning effort override for the resumed turn (e.g. none, minimal, low, medium, high, xhigh).")]
        public string? Effort { get; init; }

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
        OutputFormat format;
        try
        {
            format = settings.ResolveOutputFormat(OutputFormatUsage.Streaming);
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

        if (string.IsNullOrWhiteSpace(settings.RunId) || !Guid.TryParse(settings.RunId, out var runId))
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

        if (settings.FollowOnly && settings.Tail is not null)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("invalid_args", "--follow-only conflicts with --tail.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]--follow-only conflicts with --tail.[/]");
            }
            return 2;
        }

        if (settings.NoFollow && settings.FollowOnly)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("invalid_args", "--no-follow conflicts with --follow-only.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]--no-follow conflicts with --follow-only.[/]");
            }
            return 2;
        }

        var replay = !settings.FollowOnly;
        var follow = !settings.NoFollow;
        var tail = settings.Tail;

        var prompt = ResolvePrompt(settings);

        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        try
        {
            var resumed = await client.ResumeAsync(runId, prompt, settings.Effort, cancellationToken);
            var json = format != OutputFormat.Human;
            if (!json)
            {
                AnsiConsole.MarkupLine($"RunId: [cyan]{resumed.RunId:D}[/]  Status: [grey]{resumed.Status}[/]");
            }
            else
            {
                CliOutput.WriteJsonLine(new { eventName = "run.resumed", runId = resumed.RunId, status = resumed.Status });
            }
        }
        catch (Exception ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("resume_failed", ex.Message ?? string.Empty);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to resume run:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            }
            return 1;
        }

        if (settings.Detach)
        {
            return 0;
        }

        return await ExecCommand.StreamAsync(client, runId, replay, follow, tail, format, cancellationToken);
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

}
