using CodexD.HttpRunner.Client;
using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands;

public sealed class LsCommand : AsyncCommand<LsCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--all")]
        public bool All { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        OutputFormat format;
        try
        {
            format = settings.ResolveOutputFormat(OutputFormatUsage.Single);
        }
        catch (ArgumentException ex)
        {
            if (settings.Json)
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
        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        var runs = await client.ListRunsAsync(resolved.Cwd, settings.All, cancellationToken);

        if (format != OutputFormat.Human)
        {
            CliOutput.WriteJsonLine(new { items = runs });
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
