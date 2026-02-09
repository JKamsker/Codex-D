using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.Server;
using CodexD.HttpRunner.State;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;
using RunnerHost = CodexD.HttpRunner.Server.Host;

namespace CodexD.HttpRunner.Commands;

public sealed class ServeCommand : AsyncCommand<ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--daemon")]
        [Description("Run as a detached daemon (Windows only).")]
        public bool Daemon { get; init; }

        [CommandOption("--daemon-child")]
        [Description("Internal. Used by --daemon.")]
        public bool DaemonChild { get; init; }

        [CommandOption("--force")]
        [Description("Daemon only. Force-stop an existing daemon and reinstall binaries before starting.")]
        public bool Force { get; init; }

        [CommandOption("--daemon-version <VERSION>")]
        [Description("Internal. Used by --daemon.")]
        public string? DaemonVersion { get; init; }

        [CommandOption("--listen <IP>")]
        [Description("Listen address. Default: 127.0.0.1")]
        [DefaultValue("127.0.0.1")]
        public string Listen { get; init; } = "127.0.0.1";

        [CommandOption("--port <PORT>")]
        [Description("TCP port. Foreground default: 8787. Daemon default: 0 (ephemeral).")]
        public int? Port { get; init; }

        [CommandOption("--require-auth")]
        [Description("Require Bearer token auth even on loopback.")]
        public bool RequireAuth { get; init; }

        [CommandOption("--token <TOKEN>")]
        [Description("Override the persisted token (also persists the override).")]
        public string? Token { get; init; }

        [CommandOption("--state-dir <DIR>")]
        [Description("Override the state directory (identity + runs). Foreground default: <cwd>/.codex-d. Daemon default: %LOCALAPPDATA%/codex-d/daemon/config")]
        public string? StateDir { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Daemon || settings.DaemonChild)
        {
            if (!OperatingSystem.IsWindows())
            {
                AnsiConsole.MarkupLine("[red]Daemon mode is currently supported only on Windows.[/] Use [grey]codex-d http serve[/] (foreground) instead.");
                return 2;
            }

            if (settings.DaemonChild)
            {
                return await RunDaemonChildAsync(settings, cancellationToken);
            }

            return await RunDaemonParentAsync(settings, cancellationToken);
        }

        return await RunForegroundAsync(settings, cancellationToken);
    }

    private static async Task<int> RunForegroundAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(settings.Listen, out var listen) || listen is null)
        {
            AnsiConsole.MarkupLine($"[red]Invalid --listen IP:[/] {settings.Listen}");
            return 2;
        }

        var isDev = BuildMode.IsDev();
        var port = settings.Port ?? TryGetEnvInt("CODEX_D_FOREGROUND_PORT") ?? StatePaths.GetDefaultForegroundPort(isDev);
        if (port is <= 0 or > 65535)
        {
            AnsiConsole.MarkupLine($"[red]Invalid --port:[/] {port}");
            return 2;
        }

        var stateDirRaw = string.IsNullOrWhiteSpace(settings.StateDir)
            ? TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_FOREGROUND_STATE_DIR")) ??
              StatePaths.GetForegroundStateDir(Directory.GetCurrentDirectory())
            : settings.StateDir;

        var stateDir = Path.GetFullPath(stateDirRaw);

        var identityFile = StatePaths.IdentityFile(stateDir);
        var identityStore = new IdentityStore(identityFile);
        var identity = await identityStore.LoadOrCreateAsync(settings.Token, cancellationToken);

        var isLoopback = IPAddress.IsLoopback(listen);
        var requireAuth = settings.RequireAuth || !isLoopback;

        var displayHost = isLoopback ? "127.0.0.1" : listen.ToString();
        var baseUrl = $"http://{displayHost}:{port}";

        var config = new ServerConfig
        {
            ListenAddress = listen,
            Port = port,
            RequireAuth = requireAuth,
            BaseUrl = baseUrl,
            StateDirectory = stateDir,
            Identity = identity,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        PrintBanner(config, isLoopback);

        var app = RunnerHost.Build(config);
        using var reg = cancellationToken.Register(() => app.Lifetime.StopApplication());
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> RunDaemonParentAsync(Settings settings, CancellationToken cancellationToken)
    {
        var isDev = BuildMode.IsDev();

        if (!IPAddress.TryParse(settings.Listen, out var listen) || listen is null)
        {
            AnsiConsole.MarkupLine($"[red]Invalid --listen IP:[/] {settings.Listen}");
            return 2;
        }

        var port = settings.Port ?? TryGetEnvInt("CODEX_D_DAEMON_PORT") ?? StatePaths.DEFAULT_DAEMON_PORT;
        if (port is < 0 or > 65535)
        {
            AnsiConsole.MarkupLine($"[red]Invalid --port:[/] {port}");
            return 2;
        }

        var stateDirRaw = string.IsNullOrWhiteSpace(settings.StateDir)
            ? TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR")) ?? StatePaths.GetDefaultDaemonStateDir(isDev)
            : settings.StateDir;

        var stateDir = Path.GetFullPath(stateDirRaw);

        Directory.CreateDirectory(stateDir);

        var daemonBinDir = StatePaths.GetDaemonBinDirForStateDir(stateDir);
        var runtimePath = Path.Combine(stateDir, "daemon.runtime.json");
        var identityPath = StatePaths.IdentityFile(stateDir);

        var desiredVersion = await GetDesiredDaemonVersionAsync(isDev, cancellationToken);
        var installedVersion = TryReadMarker(Path.Combine(daemonBinDir, ".version"));

        if (DaemonRuntimeFile.TryRead(runtimePath, out var runtime) && runtime is not null)
        {
            var token = TrimOrNull(settings.Token) ?? IdentityFileReader.TryReadToken(identityPath);
            var health = await RunnerHealth.CheckAsync(runtime.BaseUrl, token, cancellationToken);

            if (health == RunnerHealthStatus.Ok)
            {
                if (settings.Force || (isDev && !string.Equals(installedVersion, desiredVersion, StringComparison.Ordinal)))
                {
                    await StopDaemonAsync(runtimePath, runtime.Pid, cancellationToken);
                }
                else
                {
                    AnsiConsole.Write(new Rule("[bold]codex-d http serve -d[/]").LeftJustified());
                    AnsiConsole.MarkupLine($"Daemon URL: [cyan]{runtime.BaseUrl}[/]");
                    AnsiConsole.MarkupLine($"StateDir: [grey]{stateDir}[/]");
                    AnsiConsole.WriteLine();
                    return 0;
                }
            }
            else
            {
                var running = IsProcessRunning(runtime.Pid);
                if (running && !(settings.Force || isDev))
                {
                    AnsiConsole.MarkupLine("[red]Daemon appears to be running but is unreachable.[/] Use [grey]--force[/] to stop and replace it.");
                    AnsiConsole.MarkupLine($"StateDir: [grey]{stateDir}[/]");
                    return 1;
                }

                if (running)
                {
                    await StopDaemonAsync(runtimePath, runtime.Pid, cancellationToken);
                }
                else
                {
                    TryDeleteFile(runtimePath);
                }
            }
        }

        var forceInstall =
            settings.Force ||
            (isDev && !string.Equals(installedVersion, desiredVersion, StringComparison.Ordinal));

        if (!isDev && !settings.Force)
        {
            // Non-dev: only update binaries if daemon is stopped and versions differ.
            if (string.Equals(installedVersion, desiredVersion, StringComparison.Ordinal))
            {
                forceInstall = false;
            }
        }

        var args = new List<string>
        {
            "http",
            "serve",
            "--daemon-child",
            "--daemon-version",
            desiredVersion,
            "--listen",
            settings.Listen,
            "--port",
            port.ToString(),
            "--state-dir",
            stateDir,
            "--require-auth"
        };

        if (!string.IsNullOrWhiteSpace(settings.Token))
        {
            args.Add("--token");
            args.Add(settings.Token.Trim());
        }

        await DaemonSelfInstaller.InstallSelfAsync(daemonBinDir, desiredVersion, forceInstall, cancellationToken);

        var psi = DaemonSelfInstaller.CreateInstalledStartInfo(daemonBinDir, args);
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;

        try
        {
            _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start daemon child:[/] {ex.Message}");
            return 1;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (DaemonRuntimeFile.TryRead(runtimePath, out var rt) && rt is not null)
            {
                var token = TrimOrNull(settings.Token) ?? IdentityFileReader.TryReadToken(identityPath);
                if (await RunnerHealth.IsHealthyAsync(rt.BaseUrl, token, cancellationToken))
                {
                    AnsiConsole.Write(new Rule("[bold]codex-d http serve -d[/]").LeftJustified());
                    AnsiConsole.MarkupLine($"Daemon URL: [cyan]{rt.BaseUrl}[/]");
                    AnsiConsole.MarkupLine($"StateDir: [grey]{stateDir}[/]");
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"Try: [grey]codex-d http exec \"Hello\"[/]");
                    AnsiConsole.WriteLine();
                    return 0;
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        AnsiConsole.MarkupLine("[red]Failed to start daemon:[/] runtime file or health check did not become ready in time.");
        AnsiConsole.MarkupLine($"Expected runtime file: [grey]{runtimePath}[/]");
        return 1;
    }

    private static async Task<int> RunDaemonChildAsync(Settings settings, CancellationToken cancellationToken)
    {
        var isDev = BuildMode.IsDev();

        if (!IPAddress.TryParse(settings.Listen, out var listen) || listen is null)
        {
            return 2;
        }

        var port = settings.Port ?? TryGetEnvInt("CODEX_D_DAEMON_PORT") ?? StatePaths.DEFAULT_DAEMON_PORT;
        if (port is < 0 or > 65535)
        {
            return 2;
        }

        var stateDirRaw = string.IsNullOrWhiteSpace(settings.StateDir)
            ? TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR")) ?? StatePaths.GetDefaultDaemonStateDir(isDev)
            : settings.StateDir;

        var stateDir = Path.GetFullPath(stateDirRaw);

        var identityFile = StatePaths.IdentityFile(stateDir);
        var identityStore = new IdentityStore(identityFile);
        var identity = await identityStore.LoadOrCreateAsync(settings.Token, cancellationToken);

        var isLoopback = IPAddress.IsLoopback(listen);
        var displayHost = isLoopback ? "127.0.0.1" : listen.ToString();
        var baseUrl = $"http://{displayHost}:{port}";

        var config = new ServerConfig
        {
            ListenAddress = listen,
            Port = port,
            RequireAuth = true,
            BaseUrl = baseUrl,
            StateDirectory = stateDir,
            Identity = identity,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var app = RunnerHost.Build(config);
        using var reg = cancellationToken.Register(() => app.Lifetime.StopApplication());

        await app.StartAsync(cancellationToken);

        var actualPort = TryResolveBoundPort(app);
        if (port == 0 && actualPort == 0)
        {
            return 1;
        }

        var runtime = new DaemonRuntimeInfo
        {
            BaseUrl = $"http://{displayHost}:{(actualPort == 0 ? port : actualPort)}",
            Listen = listen.ToString(),
            Port = actualPort == 0 ? port : actualPort,
            Pid = Environment.ProcessId,
            StartedAtUtc = config.StartedAtUtc,
            StateDir = stateDir,
            Version = TrimOrNull(settings.DaemonVersion) ?? typeof(ServeCommand).Assembly.GetName().Version?.ToString() ?? "0.0.0"
        };

        await DaemonRuntimeFile.WriteAtomicAsync(Path.Combine(stateDir, "daemon.runtime.json"), runtime, cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);
        return 0;
    }

    private static async Task<string> GetDesiredDaemonVersionAsync(bool isDev, CancellationToken ct)
    {
        var assemblyVersion = typeof(ServeCommand).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        if (!isDev)
        {
            return assemblyVersion;
        }

        var computed = await DevVersionComputer.TryComputeAsync(Directory.GetCurrentDirectory(), ct);
        return string.IsNullOrWhiteSpace(computed) ? assemblyVersion : computed.Trim();
    }

    private static async Task StopDaemonAsync(string runtimePath, int pid, CancellationToken ct)
    {
        if (pid > 0)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                try
                {
                    await p.WaitForExitAsync(ct);
                }
                catch
                {
                    // ignore
                }
            }
            catch
            {
                // ignore
            }
        }

        TryDeleteFile(runtimePath);
    }

    private static bool IsProcessRunning(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadMarker(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath))
            {
                return null;
            }

            var text = File.ReadAllText(markerPath);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void PrintBanner(ServerConfig config, bool isLoopback)
    {
        AnsiConsole.Write(new Rule("[bold]codex-d http serve[/]").LeftJustified());
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
        AnsiConsole.MarkupLine($"Try: [grey]codex-d http exec --url {config.BaseUrl} --cd \"{Directory.GetCurrentDirectory()}\" \"Hello\"[/]");
        if (config.RequireAuth)
        {
            AnsiConsole.MarkupLine($"     [grey]... --token {config.Identity.Token}[/]");
        }
        AnsiConsole.WriteLine();
    }

    private static int TryResolveBoundPort(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        try
        {
            var server = app.Services.GetService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer)) as Microsoft.AspNetCore.Hosting.Server.IServer;
            var feature = server?.Features?.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
            var addr = feature?.Addresses?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(addr))
            {
                return 0;
            }

            if (!Uri.TryCreate(addr, UriKind.Absolute, out var uri))
            {
                return 0;
            }

            return uri.Port;
        }
        catch
        {
            return 0;
        }
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? TryGetEnvInt(string name)
    {
        var raw = TrimOrNull(Environment.GetEnvironmentVariable(name));
        return int.TryParse(raw, out var i) ? i : null;
    }
}
