using System.ComponentModel;
using System.Text.Json;
using CodexD.HttpRunner.Client;
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

        var prompt = ResolvePrompt(settings);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AnsiConsole.MarkupLine("[red]Missing steer prompt.[/]");
            return 2;
        }

        try
        {
            await client.SteerAsync(runId, prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to steer run:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            return 1;
        }

        if (settings.Json)
        {
            WriteJsonLine(new { eventName = "run.steered", runId });
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

    private static void WriteJsonLine(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Console.Out.WriteLine(json);
    }
}

