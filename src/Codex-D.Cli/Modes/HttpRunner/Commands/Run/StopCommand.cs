using System.ComponentModel;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Run;

public sealed class StopCommand : AsyncCommand<StopCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
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

        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        try
        {
            await client.StopAsync(runId, cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to stop run:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            return 1;
        }

        if (settings.Json)
        {
            WriteJsonLine(new { eventName = "run.stop_requested", runId });
        }
        else
        {
            AnsiConsole.MarkupLine($"Stop requested: [cyan]{runId:D}[/]");
        }

        return 0;
    }

    private static void WriteJsonLine(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Console.Out.WriteLine(json);
    }
}
