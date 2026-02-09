using System.ComponentModel;
using CodexD.CloudRunner.Commands;
using CodexD.CloudRunner.Configuration;
using CodexD.CloudRunner.Connection;
using CodexD.CloudRunner.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.CloudRunner.Cli;

public sealed class ServeCommand : AsyncCommand<ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--server-url <URL>")]
        [Description("CodexWebUi.Api base URL. Env: CODEXWEBUI_RUNNER_SERVER_URL")]
        public string? ServerUrl { get; init; }

        [CommandOption("--api-key <KEY>")]
        [Description("Runner API key. Env: CODEXWEBUI_RUNNER_API_KEY")]
        public string? ApiKey { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Runner display name. Env: CODEXWEBUI_RUNNER_NAME (default: hostname)")]
        public string? Name { get; init; }

        [CommandOption("--identity-file <PATH>")]
        [Description("Identity file path. Env: CODEXWEBUI_RUNNER_IDENTITY_FILE")]
        public string? IdentityFile { get; init; }

        [CommandOption("--workspace-root <PATH>")]
        [Description("Allowed workspace root (repeatable). Env: CODEXWEBUI_RUNNER_WORKSPACE_ROOTS (semicolon-separated)")]
        public string[] WorkspaceRoot { get; init; } = [];

        [CommandOption("--heartbeat-interval <DURATION>")]
        [Description("Heartbeat interval. Examples: 5s, 250ms, 00:00:05. Env: CODEXWEBUI_RUNNER_HEARTBEAT_INTERVAL")]
        public string? HeartbeatInterval { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AppOptions options;
        try
        {
            options = ResolveOptions(settings);
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder(args: Array.Empty<string>());

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(x =>
        {
            x.SingleLine = true;
            x.TimestampFormat = "HH:mm:ss ";
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IdentityStore>();
        builder.Services.AddSingleton<CommandRouter>();
        builder.Services.AddHostedService<ConnectionService>();

        var host = builder.Build();

        try
        {
            await host.RunAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Runner");
            logger.LogError(ex, "Runner terminated unexpectedly.");
            return 1;
        }
    }

    private static AppOptions ResolveOptions(Settings settings)
    {
        var envServerUrl = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_SERVER_URL");
        var envApiKey = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_API_KEY");
        var envName = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_NAME");
        var envIdentityFile = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_IDENTITY_FILE");
        var envWorkspaceRoots = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_WORKSPACE_ROOTS");
        var envHeartbeatInterval = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_HEARTBEAT_INTERVAL");

        var serverUrl = string.IsNullOrWhiteSpace(settings.ServerUrl) ? envServerUrl : settings.ServerUrl;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new ConfigurationException("Missing server URL. Set CODEXWEBUI_RUNNER_SERVER_URL or pass --server-url.");
        }

        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var server))
        {
            throw new ConfigurationException($"Invalid server URL: '{serverUrl}'.");
        }

        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? envApiKey : settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ConfigurationException("Missing API key. Set CODEXWEBUI_RUNNER_API_KEY or pass --api-key.");
        }

        var name = string.IsNullOrWhiteSpace(settings.Name) ? envName : settings.Name;
        name ??= Environment.MachineName;
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.MachineName;
        }

        var identityFile = string.IsNullOrWhiteSpace(settings.IdentityFile) ? envIdentityFile : settings.IdentityFile;
        identityFile = string.IsNullOrWhiteSpace(identityFile) ? GetDefaultIdentityFile() : identityFile.Trim();

        var workspaceRoots = ParseWorkspaceRoots(envWorkspaceRoots);
        if (settings.WorkspaceRoot.Length > 0)
        {
            workspaceRoots.AddRange(settings.WorkspaceRoot);
        }

        workspaceRoots = workspaceRoots
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var heartbeatInterval = ParseDurationOrDefault(
            string.IsNullOrWhiteSpace(settings.HeartbeatInterval) ? envHeartbeatInterval : settings.HeartbeatInterval,
            fallback: TimeSpan.FromSeconds(5));

        return new AppOptions
        {
            ServerUrl = server,
            ApiKey = apiKey.Trim(),
            Name = name,
            IdentityFile = identityFile,
            WorkspaceRoots = workspaceRoots,
            HeartbeatInterval = heartbeatInterval
        };
    }

    private static List<string> ParseWorkspaceRoots(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TimeSpan ParseDurationOrDefault(string? raw, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        raw = raw.Trim();

        if (TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var ts))
        {
            return ts;
        }

        if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(raw[..^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        if (raw.EndsWith('s') &&
            double.TryParse(raw[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
        {
            return TimeSpan.FromSeconds(s);
        }

        if (raw.EndsWith('m') &&
            double.TryParse(raw[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m))
        {
            return TimeSpan.FromMinutes(m);
        }

        if (raw.EndsWith('h') &&
            double.TryParse(raw[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h))
        {
            return TimeSpan.FromHours(h);
        }

        throw new ConfigurationException($"Invalid duration for 'CODEXWEBUI_RUNNER_HEARTBEAT_INTERVAL': '{raw}'. Examples: '00:00:05', '5s', '250ms'.");
    }

    private static string GetDefaultIdentityFile()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = AppContext.BaseDirectory;
        }

        return Path.Combine(baseDir, "Codex-D", "identity.json");
    }
}

