using System.ComponentModel;
using System.Net;
using CodexWebUi.Runner.HttpRunner.Server;
using CodexWebUi.Runner.HttpRunner.State;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexWebUi.Runner.HttpRunner.Commands;

public sealed class ServeCommand : AsyncCommand<ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--listen <IP>")]
        [Description("Listen address. Default: 127.0.0.1")]
        [DefaultValue("127.0.0.1")]
        public string Listen { get; init; } = "127.0.0.1";

        [CommandOption("--port <PORT>")]
        [Description("TCP port. Default: 8787")]
        [DefaultValue(8787)]
        public int Port { get; init; } = 8787;

        [CommandOption("--require-auth")]
        [Description("Require Bearer token auth even on loopback.")]
        public bool RequireAuth { get; init; }

        [CommandOption("--token <TOKEN>")]
        [Description("Override the persisted token (also persists the override).")]
        public string? Token { get; init; }

        [CommandOption("--state-dir <DIR>")]
        [Description("Override the state directory (identity + runs).")]
        public string? StateDir { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(settings.Listen, out var listen) || listen is null)
        {
            AnsiConsole.MarkupLine($"[red]Invalid --listen IP:[/] {settings.Listen}");
            return 2;
        }

        if (settings.Port is <= 0 or > 65535)
        {
            AnsiConsole.MarkupLine($"[red]Invalid --port:[/] {settings.Port}");
            return 2;
        }

        var stateDir = string.IsNullOrWhiteSpace(settings.StateDir)
            ? StatePaths.GetDefaultStateDirectory()
            : Path.GetFullPath(settings.StateDir);

        var identityFile = StatePaths.IdentityFile(stateDir);
        var identityStore = new IdentityStore(identityFile);
        var identity = await identityStore.LoadOrCreateAsync(settings.Token, cancellationToken);

        var isLoopback = IPAddress.IsLoopback(listen);
        var requireAuth = settings.RequireAuth || !isLoopback;

        var displayHost = isLoopback ? "127.0.0.1" : listen.ToString();
        var baseUrl = $"http://{displayHost}:{settings.Port}";

        var config = new ServerConfig
        {
            ListenAddress = listen,
            Port = settings.Port,
            RequireAuth = requireAuth,
            BaseUrl = baseUrl,
            StateDirectory = stateDir,
            Identity = identity,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        PrintBanner(config, isLoopback);

        var app = Host.Build(config);
        using var reg = cancellationToken.Register(() => app.Lifetime.StopApplication());
        await app.RunAsync();
        return 0;
    }

    private static void PrintBanner(ServerConfig config, bool isLoopback)
    {
        AnsiConsole.Write(new Rule("[bold]codex-runner http serve[/]").LeftJustified());
        AnsiConsole.MarkupLine($"Base URL: [cyan]{config.BaseUrl}[/]");
        AnsiConsole.MarkupLine($"RunnerId: [cyan]{config.Identity.RunnerId}[/]");
        AnsiConsole.MarkupLine($"StateDir: [grey]{config.StateDirectory}[/]");

        if (config.RequireAuth)
        {
            AnsiConsole.MarkupLine("Auth: [yellow]required[/] (Authorization: Bearer <token>)");
        }
        else
        {
            AnsiConsole.MarkupLine("Auth: [green]not required[/] on loopback (token still accepted)");
        }

        AnsiConsole.MarkupLine($"Token: [yellow]{config.Identity.Token}[/]");

        if (!isLoopback)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] Non-loopback listen requires the token. Treat it like a password.");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Try: [grey]codex-runner http exec --url {config.BaseUrl} --cd \"{Directory.GetCurrentDirectory()}\" \"Hello\"[/]");
        if (config.RequireAuth)
        {
            AnsiConsole.MarkupLine($"     [grey]... --token {config.Identity.Token}[/]");
        }
        AnsiConsole.WriteLine();
    }
}
