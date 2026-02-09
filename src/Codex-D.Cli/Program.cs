using CodexD.HttpRunner.Commands;
using Spectre.Console;
using Spectre.Console.Cli;
using CloudServeCommand = CodexD.CloudRunner.Cli.ServeCommand;
using CodexD.HttpRunner.Commands.Runs;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("codex-d");

    config.AddBranch("http", http =>
    {
        http.SetDescription("Standalone HTTP + SSE runner.");

        http.AddBranch("runs", runs =>
        {
            runs.SetDescription("Inspect local run logs (events.jsonl) from a state directory.");

            runs.AddCommand<ThinkingSummariesCommand>("thinking")
                .WithDescription("Print one-line summaries from thinking blocks (bold **...** headings).");

            runs.AddCommand<MessagesCommand>("messages")
                .WithDescription("Print the last completed agent message(s) from a run.");
        });

        http.AddCommand<ServeCommand>("serve")
            .WithDescription("Start the HTTP/SSE runner.");

        http.AddCommand<ExecCommand>("exec")
            .WithDescription("Start a new run via HTTP (optionally detached).");

        http.AddCommand<ReviewCommand>("review")
            .WithDescription("Run a non-interactive `codex review` on the runner and stream output.");

        http.AddCommand<AttachCommand>("attach")
            .WithDescription("Attach to an existing run and stream SSE output.");

        http.AddCommand<LsCommand>("ls")
            .WithDescription("List runs known by the server.");
    });

    config.AddBranch("cloud", cloud =>
    {
        cloud.SetDescription("Runner that connects to CodexWebUi.Api via SignalR.");

        cloud.AddCommand<CloudServeCommand>("serve")
            .WithDescription("Run the Cloud runner (connects to CodexWebUi.Api via SignalR).");
    });
});

if (args.Length == 0)
{
    AnsiConsole.MarkupLine("[red]Missing mode.[/]");
    AnsiConsole.MarkupLine("Use: [grey]codex-d http --help[/] or [grey]codex-d cloud --help[/]");
    AnsiConsole.WriteLine();
    await app.RunAsync(["--help"]);
    return 2;
}

if (args.Length == 1 &&
    (string.Equals(args[0], "http", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(args[0], "cloud", StringComparison.OrdinalIgnoreCase)))
{
    return await app.RunAsync([args[0], "--help"]);
}

if (args.Length > 1 &&
    (string.Equals(args[0], "http", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(args[0], "cloud", StringComparison.OrdinalIgnoreCase)) &&
    args[1].StartsWith("--", StringComparison.Ordinal))
{
    AnsiConsole.MarkupLine("[red]Missing subcommand.[/]");
    AnsiConsole.MarkupLine($"Use: [grey]codex-d {args[0]} <command> ...[/]");
    AnsiConsole.WriteLine();
    await app.RunAsync([args[0], "--help"]);
    return 2;
}

return await app.RunAsync(args);
