using System.ComponentModel;
using CodexD.Shared.Paths;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Client;

public abstract class ClientSettingsBase : CommandSettings
{
    [CommandOption("--url <URL>")]
    [Description("Runner base URL. Default: env CODEX_RUNNER_URL or http://127.0.0.1:8787")]
    public string? Url { get; init; }

    [CommandOption("--token <TOKEN>")]
    [Description("Bearer token. Default: env CODEX_RUNNER_TOKEN")]
    public string? Token { get; init; }

    [CommandOption("--cd <DIR>")]
    [Description("Working directory (exact-match filtering for ls/--last). Default: current directory")]
    public string? Cd { get; init; }

    [CommandOption("--json")]
    [Description("Print JSONL events (client-side envelope) instead of human-friendly output.")]
    public bool Json { get; init; }

    public ResolvedClientSettings Resolve()
    {
        var url =
            Url ??
            Environment.GetEnvironmentVariable("CODEX_RUNNER_URL") ??
            "http://127.0.0.1:8787";

        var token =
            Token ??
            Environment.GetEnvironmentVariable("CODEX_RUNNER_TOKEN");

        var cwd = string.IsNullOrWhiteSpace(Cd) ? Directory.GetCurrentDirectory() : Cd!;
        cwd = PathPolicy.TrimTrailingSeparators(Path.GetFullPath(cwd));

        return new ResolvedClientSettings { BaseUrl = url, Token = token, Cwd = cwd };
    }
}
