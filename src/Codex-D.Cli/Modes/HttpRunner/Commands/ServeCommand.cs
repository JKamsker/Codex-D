using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.Server;
using CodexD.HttpRunner.State;
using CodexD.Shared.Output;
using CodexD.Shared.Strings;
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

        [CommandOption("--persist-raw-events")]
        [Description("Persist raw differential events to events.jsonl (debugging). Default: false (in-memory backlog + Codex rollout replay).")]
        public bool PersistRawEvents { get; init; }

        [CommandOption("--output-format|--outputformat <FORMAT>")]
        [Description("Output format: human, json, or jsonl. Default: human. For long-running commands, 'json' behaves like 'jsonl'.")]
        public string? OutputFormat { get; init; }

        [CommandOption("--json")]
        [Description("Deprecated. Use --outputformat json/jsonl.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        OutputFormat format;
        try
        {
            format = OutputFormatParser.Resolve(settings.OutputFormat, settings.Json, OutputFormatUsage.Streaming);
        }
        catch (ArgumentException ex)
        {
            if (settings.Json || !string.IsNullOrWhiteSpace(settings.OutputFormat))
            {
                CliOutput.WriteJsonError("invalid_outputformat", ex.Message);
                return 2;
            }

            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var json = format != OutputFormat.Human;

        if (settings.Daemon || settings.DaemonChild)
        {
            if (!OperatingSystem.IsWindows())
            {
                if (json)
                {
                    CliOutput.WriteJsonError("unsupported", "Daemon mode is currently supported only on Windows. Use `codex-d serve` (foreground) instead.");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Daemon mode is currently supported only on Windows.[/] Use [grey]codex-d serve[/] (foreground) instead.");
                }
                return 2;
            }

            if (settings.DaemonChild)
            {
                return await RunDaemonChildAsync(settings, format, cancellationToken);
            }

            return await RunDaemonParentAsync(settings, format, cancellationToken);
        }

        return await RunForegroundAsync(settings, format, cancellationToken);
    }

    private static async Task<int> RunForegroundAsync(Settings settings, OutputFormat format, CancellationToken cancellationToken)
    {
        var json = format != OutputFormat.Human;

        if (!IPAddress.TryParse(settings.Listen, out var listen) || listen is null)
        {
            if (json)
            {
                CliOutput.WriteJsonError("invalid_listen", $"Invalid --listen IP: {settings.Listen}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --listen IP:[/] {Markup.Escape(settings.Listen)}");
            }
            return 2;
        }

        var isDev = BuildMode.IsDev();
        var port = settings.Port ?? TryGetEnvInt("CODEX_D_FOREGROUND_PORT") ?? StatePaths.GetDefaultForegroundPort(isDev);
        if (port is <= 0 or > 65535)
        {
            if (json)
            {
                CliOutput.WriteJsonError("invalid_port", $"Invalid --port: {port}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --port:[/] {port}");
            }
            return 2;
        }

        var stateDirRaw = string.IsNullOrWhiteSpace(settings.StateDir)
            ? StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_FOREGROUND_STATE_DIR")) ??
              StatePaths.GetForegroundStateDir(Directory.GetCurrentDirectory())
            : settings.StateDir;

        var stateDir = Path.GetFullPath(stateDirRaw);

        var identityFile = StatePaths.IdentityFile(stateDir);
        var identityStore = new IdentityStore(identityFile);
        var tokenOverride = StringHelpers.TrimOrNull(settings.Token) ?? TryGetEnvTokenOverride();
        var identity = await identityStore.LoadOrCreateAsync(tokenOverride, cancellationToken);

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
            StartedAtUtc = DateTimeOffset.UtcNow,
            PersistRawEvents = settings.PersistRawEvents || (TryGetEnvBool("CODEX_D_PERSIST_RAW_EVENTS") ?? false),
            JsonLogs = json
        };

        if (json)
        {
            CliOutput.WriteJsonLine(new
            {
                eventName = "server.started",
                baseUrl = config.BaseUrl,
                runnerId = config.Identity.RunnerId,
                stateDir = config.StateDirectory,
                requireAuth = config.RequireAuth,
                listen = config.ListenAddress.ToString(),
                port = config.Port
            });
        }
        else
        {
            PrintBanner(config, isLoopback);
        }

        var app = RunnerHost.Build(config);
        using var reg = cancellationToken.Register(() => app.Lifetime.StopApplication());
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> RunDaemonParentAsync(Settings settings, OutputFormat format, CancellationToken cancellationToken)
    {
        var json = format != OutputFormat.Human;
        var isDev = BuildMode.IsDev();

        if (!IPAddress.TryParse(settings.Listen, out var listen) || listen is null)
        {
            if (json)
            {
                CliOutput.WriteJsonError("invalid_listen", $"Invalid --listen IP: {settings.Listen}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --listen IP:[/] {Markup.Escape(settings.Listen)}");
            }
            return 2;
        }

        var port = settings.Port ?? TryGetEnvInt("CODEX_D_DAEMON_PORT") ?? StatePaths.DEFAULT_DAEMON_PORT;
        if (port is < 0 or > 65535)
        {
            if (json)
            {
                CliOutput.WriteJsonError("invalid_port", $"Invalid --port: {port}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --port:[/] {port}");
            }
            return 2;
        }

        var stateDirRaw = string.IsNullOrWhiteSpace(settings.StateDir)
            ? StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR")) ?? StatePaths.GetDefaultDaemonStateDir(isDev)
            : settings.StateDir;

        var stateDir = Path.GetFullPath(stateDirRaw);

        Directory.CreateDirectory(stateDir);

        var daemonBinDir = StatePaths.GetDaemonBinDirForStateDir(stateDir);
        var runtimePath = Path.Combine(stateDir, "daemon.runtime.json");
        var identityPath = StatePaths.IdentityFile(stateDir);

        var desiredVersion = await GetDesiredDaemonVersionAsync(isDev, cancellationToken);
        var installedVersion = TryReadMarker(Path.Combine(daemonBinDir, ".version"));
        var tokenOverride = StringHelpers.TrimOrNull(settings.Token) ?? TryGetEnvTokenOverride();

        if (settings.Force || isDev || !string.Equals(installedVersion, desiredVersion, StringComparison.Ordinal))
        {
            var installedDisplay = installedVersion ?? "<none>";
            if (json)
            {
                CliOutput.WriteJsonLine(new
                {
                    eventName = "daemon.version",
                    installDir = daemonBinDir,
                    desired = desiredVersion,
                    installed = installedDisplay
                });
            }
            else
            {
                AnsiConsole.MarkupLine($"[grey]Daemon install dir:[/] {Markup.Escape(daemonBinDir)}");
                AnsiConsole.MarkupLine($"[grey]Daemon version:[/] desired={Markup.Escape(desiredVersion)} installed={Markup.Escape(installedDisplay)}");
            }
        }

        if (DaemonRuntimeFile.TryRead(runtimePath, out var runtime) && runtime is not null)
        {
            var token = tokenOverride ?? IdentityFileReader.TryReadToken(identityPath);
            var health = await RunnerHealth.CheckAsync(runtime.BaseUrl, token, cancellationToken);

            if (health == RunnerHealthStatus.Ok)
            {
                var needsDevReplace =
                    isDev &&
                    (!string.Equals(installedVersion, desiredVersion, StringComparison.Ordinal) ||
                     !string.Equals(runtime.Version, desiredVersion, StringComparison.Ordinal));

                if (settings.Force || needsDevReplace)
                {
                    var reason = settings.Force ? "--force" : "dev version mismatch";
                    if (json)
                    {
                        CliOutput.WriteJsonLine(new { eventName = "daemon.stop", pid = runtime.Pid, reason });
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[grey]Stopping existing daemon (pid {runtime.Pid}) due to {reason}...[/]");
                    }
                    await StopDaemonAsync(runtimePath, runtime.Pid, cancellationToken);
                }
                else
                {
                    if (json)
                    {
                        CliOutput.WriteJsonLine(new { eventName = "daemon.already_running", baseUrl = runtime.BaseUrl, stateDir });
                    }
                    else
                    {
                        AnsiConsole.Write(new Rule("[bold]codex-d serve -d[/]").LeftJustified());
                        AnsiConsole.MarkupLine($"Daemon URL: [cyan]{Markup.Escape(runtime.BaseUrl)}[/]");
                        AnsiConsole.MarkupLine($"StateDir: [grey]{Markup.Escape(stateDir)}[/]");
                        AnsiConsole.WriteLine();
                    }
                    return 0;
                }
            }
            else
            {
                var running = IsProcessRunning(runtime.Pid);
                if (running && !(settings.Force || isDev))
                {
                    if (json)
                    {
                        CliOutput.WriteJsonError(
                            "daemon_unreachable",
                            "Daemon appears to be running but is unreachable. Use --force to stop and replace it.",
                            new { stateDir });
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[red]Daemon appears to be running but is unreachable.[/] Use [grey]--force[/] to stop and replace it.");
                        AnsiConsole.MarkupLine($"StateDir: [grey]{Markup.Escape(stateDir)}[/]");
                    }
                    return 1;
                }

                if (running)
                {
                    var reason = settings.Force ? "--force" : "dev mode restart";
                    if (json)
                    {
                        CliOutput.WriteJsonLine(new { eventName = "daemon.stop", pid = runtime.Pid, reason });
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[grey]Stopping unreachable daemon (pid {runtime.Pid}) due to {reason}...[/]");
                    }
                    await StopDaemonAsync(runtimePath, runtime.Pid, cancellationToken);
                }
                else
                {
                    if (json)
                    {
                        CliOutput.WriteJsonLine(new { eventName = "daemon.runtime.remove_stale", runtimePath });
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[grey]Removing stale daemon runtime file...[/]");
                    }
                    TryDeleteFile(runtimePath);
                }
            }
        }

        var shouldInstall = settings.Force || !string.Equals(installedVersion, desiredVersion, StringComparison.Ordinal);
        var installForce = settings.Force;

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

        if (settings.PersistRawEvents || (TryGetEnvBool("CODEX_D_PERSIST_RAW_EVENTS") ?? false))
        {
            args.Add("--persist-raw-events");
        }

        if (format != OutputFormat.Human)
        {
            args.Add("--outputformat");
            args.Add(format == OutputFormat.Json ? "json" : "jsonl");
        }

        try
        {
            if (shouldInstall)
            {
                var reason = settings.Force ? "--force" : "version mismatch";
                if (json)
                {
                    CliOutput.WriteJsonLine(new { eventName = "daemon.install", reason });
                }
                else
                {
                    AnsiConsole.MarkupLine($"[grey]Installing daemon binaries ({reason})...[/]");
                }

                var copied = await DaemonSelfInstaller.InstallSelfAsync(daemonBinDir, desiredVersion, installForce, cancellationToken);
                if (copied)
                {
                    if (json)
                    {
                        CliOutput.WriteJsonLine(new { eventName = "daemon.install.completed", copied = true });
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[grey]Copied daemon binaries and updated .version marker.[/]");
                    }
                }
                else
                {
                    if (json)
                    {
                        CliOutput.WriteJsonLine(new { eventName = "daemon.install.completed", copied = false });
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[grey]Daemon binaries already up-to-date; no copy needed.[/]");
                    }
                }
            }
            else
            {
                if (json)
                {
                    CliOutput.WriteJsonLine(new { eventName = "daemon.install.skipped" });
                }
                else
                {
                    AnsiConsole.MarkupLine("[grey]Daemon binaries already up-to-date; starting daemon.[/]");
                }
            }
        }
        catch (Exception ex)
        {
            if (json)
            {
                CliOutput.WriteJsonError("daemon_install_failed", ex.Message ?? string.Empty);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to install daemon binaries:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            }
            return 1;
        }

        var psi = DaemonSelfInstaller.CreateInstalledStartInfo(daemonBinDir, args);
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;

        if (!string.IsNullOrWhiteSpace(tokenOverride))
        {
            psi.Environment["CODEX_D_TOKEN"] = tokenOverride;
        }

        try
        {
            var process = Process.Start(psi);
            if (process is null)
            {
                if (json)
                {
                    CliOutput.WriteJsonError("daemon_start_failed", "Failed to start daemon child: Process.Start returned null.");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Failed to start daemon child:[/] Process.Start returned null.");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            if (json)
            {
                CliOutput.WriteJsonError("daemon_start_failed", ex.Message ?? string.Empty);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to start daemon child:[/] {Markup.Escape(ex.Message ?? string.Empty)}");
            }
            return 1;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (DaemonRuntimeFile.TryRead(runtimePath, out var rt) && rt is not null)
            {
                var token = tokenOverride ?? IdentityFileReader.TryReadToken(identityPath);
                if (await RunnerHealth.IsHealthyAsync(rt.BaseUrl, token, cancellationToken))
                {
                    if (json)
                    {
                        CliOutput.WriteJsonLine(new { eventName = "daemon.started", baseUrl = rt.BaseUrl, stateDir });
                    }
                    else
                    {
                        AnsiConsole.Write(new Rule("[bold]codex-d serve -d[/]").LeftJustified());
                        AnsiConsole.MarkupLine($"Daemon URL: [cyan]{Markup.Escape(rt.BaseUrl)}[/]");
                        AnsiConsole.MarkupLine($"StateDir: [grey]{Markup.Escape(stateDir)}[/]");
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"Try: [grey]codex-d exec \"Hello\"[/]");
                        AnsiConsole.WriteLine();
                    }
                    return 0;
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        if (json)
        {
            CliOutput.WriteJsonError(
                "daemon_start_timeout",
                "Failed to start daemon: runtime file or health check did not become ready in time.",
                new { runtimePath });
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Failed to start daemon:[/] runtime file or health check did not become ready in time.");
            AnsiConsole.MarkupLine($"Expected runtime file: [grey]{Markup.Escape(runtimePath)}[/]");
        }
        return 1;
    }

    private static async Task<int> RunDaemonChildAsync(Settings settings, OutputFormat format, CancellationToken cancellationToken)
    {
        var json = format != OutputFormat.Human;
        var isDev = BuildMode.IsDev();

        if (!IPAddress.TryParse(settings.Listen, out var listen) || listen is null)
        {
            if (json)
            {
                CliOutput.WriteJsonError("invalid_listen", $"Invalid --listen IP: {settings.Listen}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --listen IP:[/] {Markup.Escape(settings.Listen)}");
            }
            return 2;
        }

        var port = settings.Port ?? TryGetEnvInt("CODEX_D_DAEMON_PORT") ?? StatePaths.DEFAULT_DAEMON_PORT;
        if (port is < 0 or > 65535)
        {
            if (json)
            {
                CliOutput.WriteJsonError("invalid_port", $"Invalid --port: {port}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --port:[/] {port}");
            }
            return 2;
        }

        var stateDirRaw = string.IsNullOrWhiteSpace(settings.StateDir)
            ? StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR")) ?? StatePaths.GetDefaultDaemonStateDir(isDev)
            : settings.StateDir;

        var stateDir = Path.GetFullPath(stateDirRaw);

        var identityFile = StatePaths.IdentityFile(stateDir);
        var identityStore = new IdentityStore(identityFile);
        var tokenOverride = StringHelpers.TrimOrNull(settings.Token) ?? TryGetEnvTokenOverride();
        var identity = await identityStore.LoadOrCreateAsync(tokenOverride, cancellationToken);

        var isLoopback = IPAddress.IsLoopback(listen);
        var displayHost = isLoopback ? "127.0.0.1" : listen.ToString();

        var config = new ServerConfig
        {
            ListenAddress = listen,
            Port = port,
            RequireAuth = true,
            BaseUrl = string.Empty,
            StateDirectory = stateDir,
            Identity = identity,
            StartedAtUtc = DateTimeOffset.UtcNow,
            PersistRawEvents = settings.PersistRawEvents || (TryGetEnvBool("CODEX_D_PERSIST_RAW_EVENTS") ?? false),
            JsonLogs = json
        };

        var app = RunnerHost.Build(config);
        using var reg = cancellationToken.Register(() => app.Lifetime.StopApplication());

        await app.StartAsync(cancellationToken);

        var actualPort = TryResolveBoundPort(app);
        if (port == 0 && actualPort == 0)
        {
            return 1;
        }

        var resolvedPort = actualPort == 0 ? port : actualPort;
        var baseUrl = $"http://{displayHost}:{resolvedPort}";
        config.Port = resolvedPort;
        config.BaseUrl = baseUrl;

        var runtime = new DaemonRuntimeInfo
        {
            BaseUrl = baseUrl,
            Listen = listen.ToString(),
            Port = resolvedPort,
            Pid = Environment.ProcessId,
            StartedAtUtc = config.StartedAtUtc,
            StateDir = stateDir,
            Version = StringHelpers.TrimOrNull(settings.DaemonVersion) ?? typeof(ServeCommand).Assembly.GetName().Version?.ToString() ?? "0.0.0"
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
            Process? p = null;
            try
            {
                p = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                p = null; // already stopped
            }

            if (p is not null)
            {
                using (p)
                {
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // already exited
                    }
                    catch (SystemException)
                    {
                        // ignore kill failures
                    }

                    try
                    {
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await p.WaitForExitAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore timeout
                    }
                }
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
        AnsiConsole.Write(new Rule("[bold]codex-d serve[/]").LeftJustified());
        AnsiConsole.MarkupLine($"Base URL: [cyan]{Markup.Escape(config.BaseUrl)}[/]");
        AnsiConsole.MarkupLine($"RunnerId: [cyan]{config.Identity.RunnerId}[/]");
        AnsiConsole.MarkupLine($"StateDir: [grey]{Markup.Escape(config.StateDirectory)}[/]");

        if (config.RequireAuth)
        {
            AnsiConsole.MarkupLine("Auth: [yellow]required[/] (Authorization: Bearer <token>)");
        }
        else
        {
            AnsiConsole.MarkupLine("Auth: [green]not required[/] on loopback (token still accepted)");
        }

        AnsiConsole.MarkupLine($"Token: [yellow]{Markup.Escape(config.Identity.Token)}[/]");

        if (!isLoopback)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] Non-loopback listen requires the token. Treat it like a password.");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Try: [grey]codex-d exec --url {Markup.Escape(config.BaseUrl)} --cd \"{Markup.Escape(Directory.GetCurrentDirectory())}\" \"Hello\"[/]");
        if (config.RequireAuth)
        {
            AnsiConsole.MarkupLine($"     [grey]... --token {Markup.Escape(config.Identity.Token)}[/]");
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

    private static int? TryGetEnvInt(string name)
    {
        var raw = StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable(name));
        return int.TryParse(raw, out var i) ? i : null;
    }

    private static bool? TryGetEnvBool(string name)
    {
        var raw = StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable(name));
        if (raw is null)
        {
            return null;
        }

        return raw.ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            "0" => false,
            "false" => false,
            "no" => false,
            "off" => false,
            _ => null
        };
    }

    private static string? TryGetEnvTokenOverride()
    {
        return
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_TOKEN")) ??
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_RUNNER_TOKEN"));
    }
}
