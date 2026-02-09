using Spectre.Console;
using Spectre.Console.Cli;
using CodexD.CloudRunner.Cli;
using CodexD.HttpRunner.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("codex-d");

    HttpCli.AddTo(config);
    CloudCli.AddTo(config);
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

if (args.Length == 2 &&
    (string.Equals(args[0], "http", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(args[0], "cloud", StringComparison.OrdinalIgnoreCase)) &&
    (string.Equals(args[1], "--help", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(args[1], "-h", StringComparison.OrdinalIgnoreCase)))
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
