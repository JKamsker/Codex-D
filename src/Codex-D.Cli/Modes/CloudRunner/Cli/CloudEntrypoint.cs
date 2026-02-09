using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.CloudRunner.Cli;

public static class CloudEntrypoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("codex-d cloud");

            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Run the Cloud runner (connects to CodexWebUi.Api via SignalR).");
        });

        if (args.Length == 0)
        {
            return await app.RunAsync(["--help"]);
        }

        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return await app.RunAsync(["--help"]);
        }

        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[red]Missing subcommand.[/]");
            AnsiConsole.MarkupLine("Use: [grey]codex-d cloud serve ...[/]");
            AnsiConsole.WriteLine();
            await app.RunAsync(["--help"]);
            return 2;
        }

        return await app.RunAsync(args);
    }
}
