using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;
using CodexD.CloudRunner.Cli;
using CodexD.HttpRunner.Cli;

var app = new CommandApp();
string? appVersion = null;
app.Configure(config =>
{
    config.SetApplicationName("codex-d");

    var asm = typeof(Program).Assembly;
    var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var version = string.IsNullOrWhiteSpace(informational) ? asm.GetName().Version?.ToString() : informational;
    if (!string.IsNullOrWhiteSpace(version))
    {
        config.SetApplicationVersion(version);
        appVersion = version;
    }

    HttpCli.AddTo(config);
    CloudCli.AddTo(config);
});

static bool HasArg(string[] args, string value) =>
    args.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

var wantsHelp = HasArg(args, "--help") || HasArg(args, "-h");
var wantsVersion = HasArg(args, "--version") || HasArg(args, "-v");

if (wantsHelp && !wantsVersion)
{
    var versionText = string.IsNullOrWhiteSpace(appVersion) ? "0.0.0" : appVersion;
    AnsiConsole.MarkupLine($"[grey]codex-d {Markup.Escape(versionText)}[/]");
    AnsiConsole.WriteLine();
}

if (args.Length == 0)
{
    AnsiConsole.MarkupLine("[red]Missing mode.[/]");
    AnsiConsole.MarkupLine("Use: [grey]codex-d http --help[/] or [grey]codex-d cloud --help[/]");
    AnsiConsole.WriteLine();
    if (!string.IsNullOrWhiteSpace(appVersion))
    {
        AnsiConsole.MarkupLine($"[grey]codex-d {Markup.Escape(appVersion)}[/]");
        AnsiConsole.WriteLine();
    }
    await app.RunAsync(["--help"]);
    return 2;
}

if (args.Length == 1 &&
    (string.Equals(args[0], "http", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(args[0], "cloud", StringComparison.OrdinalIgnoreCase)))
{
    if (!string.IsNullOrWhiteSpace(appVersion))
    {
        AnsiConsole.MarkupLine($"[grey]codex-d {Markup.Escape(appVersion)}[/]");
        AnsiConsole.WriteLine();
    }
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
    if (!string.IsNullOrWhiteSpace(appVersion))
    {
        AnsiConsole.MarkupLine($"[grey]codex-d {Markup.Escape(appVersion)}[/]");
        AnsiConsole.WriteLine();
    }
    await app.RunAsync([args[0], "--help"]);
    return 2;
}

return await app.RunAsync(args);
