using CodexWebUi.Runner.HttpRunner.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexWebUi.Runner.HttpRunner.Commands;

public sealed class LsCommand : AsyncCommand<LsCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--all")]
        public bool All { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var resolved = settings.Resolve();
        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        var runs = await client.ListRunsAsync(resolved.Cwd, settings.All, cancellationToken);

        if (settings.Json)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { items = runs }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Console.Out.WriteLine(json);
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("RunId");
        table.AddColumn("Created");
        table.AddColumn("Status");
        if (settings.All)
        {
            table.AddColumn("Cwd");
        }

        foreach (var r in runs.OrderByDescending(x => x.CreatedAt))
        {
            var created = r.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            if (settings.All)
            {
                table.AddRow(r.RunId.ToString("D"), created, r.Status, r.Cwd);
            }
            else
            {
                table.AddRow(r.RunId.ToString("D"), created, r.Status);
            }
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
