using System.ComponentModel;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Run;

public sealed class InterruptCommand : AsyncCommand<InterruptCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--last")]
        [Description("Interrupt the most recent run for the current --cd/cwd.")]
        public bool Last { get; init; }

        [CommandArgument(0, "[RUN_ID]")]
        public string? RunId { get; init; }
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

        try
        {
            await client.InterruptAsync(runId, cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to interrupt run:[/] {ex.Message}");
            return 1;
        }

        if (settings.Json)
        {
            WriteJsonLine(new { eventName = "run.interrupted", runId });
        }
        else
        {
            AnsiConsole.MarkupLine($"Interrupted: [cyan]{runId:D}[/]");
        }

        return 0;
    }

    private static void WriteJsonLine(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Console.Out.WriteLine(json);
    }
}

