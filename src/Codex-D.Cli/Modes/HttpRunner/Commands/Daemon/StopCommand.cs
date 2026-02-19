using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Daemon;
using CodexD.HttpRunner.State;
using CodexD.Shared.Output;
using CodexD.Shared.Paths;
using CodexD.Shared.Strings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Daemon;

public sealed class StopCommand : AsyncCommand<StopCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--state-dir <DIR>")]
        [Description("Override the daemon state directory. Default: env CODEX_D_DAEMON_STATE_DIR or %LOCALAPPDATA%/codex-d/daemon/config")]
        public string? StateDir { get; init; }

        [CommandOption("--token <TOKEN>")]
        [Description("Bearer token override (otherwise read from daemon identity.json).")]
        public string? Token { get; init; }

        [CommandOption("--force")]
        [Description("Force-stop by PID if the daemon is unreachable or shutdown fails.")]
        public bool Force { get; init; }

        [CommandOption("--output-format|--outputformat <FORMAT>")]
        [Description("Output format: human, json, or jsonl. Default: human.")]
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
            format = OutputFormatParser.Resolve(settings.OutputFormat, settings.Json, OutputFormatUsage.Single);
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
        var isDev = BuildMode.IsDev();

        var stateDirRaw =
            StringHelpers.TrimOrNull(settings.StateDir) ??
            StringHelpers.TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR")) ??
            StatePaths.GetDefaultDaemonStateDir(isDev);
        var stateDir = Path.GetFullPath(stateDirRaw);

        var runtimePath = Path.Combine(stateDir, "daemon.runtime.json");
        if (!DaemonRuntimeFile.TryRead(runtimePath, out var runtime) || runtime is null)
        {
            if (json)
            {
                CliOutput.WriteJsonError("daemon_not_running", "No daemon runtime file found.", new { stateDir, runtimePath });
            }
            else
            {
                AnsiConsole.MarkupLine("[red]No daemon runtime file found.[/]");
                AnsiConsole.MarkupLine($"StateDir: [grey]{Markup.Escape(stateDir)}[/]");
            }
            return 1;
        }

        var token =
            StringHelpers.TrimOrNull(settings.Token) ??
            IdentityFileReader.TryReadToken(StatePaths.IdentityFile(stateDir));

        var shutdownOk = await TryShutdownAsync(runtime.BaseUrl, token, cancellationToken);
        if (!shutdownOk && !settings.Force)
        {
            if (json)
            {
                CliOutput.WriteJsonError("shutdown_failed", "Failed to request shutdown. Use --force to kill by PID.", new { runtime.BaseUrl, runtime.Pid });
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Failed to request shutdown.[/] Use [grey]--force[/] to kill by PID.");
            }
            return 1;
        }

        if (!shutdownOk && settings.Force)
        {
            if (json)
            {
                CliOutput.WriteJsonLine(new { eventName = "daemon.force_kill", runtime.Pid });
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]Shutdown failed; force-killing daemon pid {runtime.Pid}...[/]");
            }

            await KillByPidAsync(runtime.Pid);
        }

        await WaitForExitAsync(runtime.Pid, cancellationToken);
        TryDeleteFile(runtimePath);

        if (json)
        {
            CliOutput.WriteJsonLine(new { eventName = "daemon.stopped", runtime.Pid, runtime.BaseUrl });
        }
        else
        {
            AnsiConsole.MarkupLine("Daemon stop requested.");
        }

        return 0;
    }

    private static async Task<bool> TryShutdownAsync(string baseUrl, string? token, CancellationToken ct)
    {
        using var http = new HttpClient();
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        try
        {
            using var res = await http.PostAsync($"{baseUrl.TrimEnd('/')}/v1/shutdown", content: null, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForExitAsync(int pid, CancellationToken ct)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch
        {
            // ignore
        }
    }

    private static Task KillByPidAsync(int pid)
    {
        if (pid <= 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        catch
        {
            // ignore
        }

        return Task.CompletedTask;
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
}

