using System.ComponentModel;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands;

public sealed class ExecCommand : AsyncCommand<ExecCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("-p|--prompt <PROMPT>")]
        [Description("Prompt text (alternative to positional PROMPT). Use '-' to read stdin.")]
        public string? PromptOption { get; init; }

        [CommandArgument(0, "[PROMPT]")]
        public string[] Prompt { get; init; } = [];

        [CommandOption("-d|--detach")]
        [Description("Detach after creating the run (does not stream output).")]
        public bool Detach { get; init; }

        [CommandOption("--model <MODEL>")]
        public string? Model { get; init; }

        [CommandOption("--sandbox <MODE>")]
        public string? Sandbox { get; init; }

        [CommandOption("--approval-policy <POLICY>")]
        [DefaultValue("never")]
        public string ApprovalPolicy { get; init; } = "never";

        [CommandOption("--uncommitted")]
        [Description("Review staged/unstaged/untracked changes (only valid with `exec review`).")]
        public bool ReviewUncommitted { get; init; }

        [CommandOption("--base <BRANCH>")]
        [Description("Review changes against base branch (only valid with `exec review`).")]
        public string? ReviewBaseBranch { get; init; }

        [CommandOption("--commit <SHA>")]
        [Description("Review a specific commit SHA (only valid with `exec review`).")]
        public string? ReviewCommitSha { get; init; }

        [CommandOption("--title <TITLE>")]
        [Description("Optional review title (only valid with `exec review`).")]
        public string? ReviewTitle { get; init; }

        [CommandOption("--review-arg <ARG>")]
        [Description("Additional raw args forwarded to `codex review` (repeatable; only valid with `exec review`).")]
        public string[] ReviewArg { get; init; } = [];

        [CommandOption("-c|--config <KEY=VALUE>")]
        [Description("Forward a `--config <key=value>` option to `codex review` (repeatable; only valid with `exec review`).")]
        public string[] ReviewConfig { get; init; } = [];

        [CommandOption("--enable <FEATURE>")]
        [Description("Forward an `--enable <feature>` option to `codex review` (repeatable; only valid with `exec review`).")]
        public string[] ReviewEnable { get; init; } = [];

        [CommandOption("--disable <FEATURE>")]
        [Description("Forward a `--disable <feature>` option to `codex review` (repeatable; only valid with `exec review`).")]
        public string[] ReviewDisable { get; init; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ResolvedClientSettings resolved;
        try
        {
            resolved = await settings.ResolveAsync(cancellationToken);
        }
        catch (RunnerResolutionFailure ex)
        {
            Console.Error.WriteLine(ex.UserMessage);
            return 1;
        }

        try
        {
            var isReview = settings.Prompt.Length > 0 &&
                           string.Equals(settings.Prompt[0], "review", StringComparison.OrdinalIgnoreCase);

            if (!isReview && UsesReviewOptions(settings))
            {
                throw new ArgumentException("Review options are only valid with `codex-d http exec review ...`.");
            }

            var prompt = isReview ? ResolveReviewPrompt(settings) : ResolvePrompt(settings);

            using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

            var request = isReview
                ? CreateReviewRequest(settings, resolved.Cwd, prompt)
                : CreateExecRequest(settings, resolved.Cwd, prompt);

            var created = await client.CreateRunAsync(request, cancellationToken);
            var runId = created.RunId;
            var status = created.Status;

            if (settings.Json)
            {
                WriteJsonLine(new { eventName = "run.created", runId, status });
            }
            else
            {
                AnsiConsole.MarkupLine($"RunId: [cyan]{runId:D}[/]  Status: [grey]{status}[/]");
            }

            if (settings.Detach)
            {
                return 0;
            }

            return await StreamAsync(client, runId, replay: true, follow: true, tail: null, json: settings.Json, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static CreateRunRequest CreateExecRequest(Settings settings, string cwd, string prompt) =>
        new()
        {
            Cwd = cwd,
            Prompt = prompt,
            Model = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim(),
            Sandbox = string.IsNullOrWhiteSpace(settings.Sandbox) ? null : settings.Sandbox.Trim(),
            ApprovalPolicy = string.IsNullOrWhiteSpace(settings.ApprovalPolicy) ? "never" : settings.ApprovalPolicy.Trim()
        };

    private static CreateRunRequest CreateReviewRequest(Settings settings, string cwd, string prompt)
    {
        var baseBranch = TrimOrNull(settings.ReviewBaseBranch);
        var commitSha = TrimOrNull(settings.ReviewCommitSha);
        var uncommitted = settings.ReviewUncommitted;

        var targets = 0;
        if (uncommitted) targets++;
        if (!string.IsNullOrWhiteSpace(baseBranch)) targets++;
        if (!string.IsNullOrWhiteSpace(commitSha)) targets++;

        if (targets == 0)
        {
            uncommitted = true;
        }

        if (targets > 1)
        {
            throw new ArgumentException("Only one of --uncommitted, --base, or --commit can be specified for `exec review`.");
        }

        var review = new RunReviewRequest
        {
            Uncommitted = uncommitted,
            BaseBranch = baseBranch,
            CommitSha = commitSha,
            Title = TrimOrNull(settings.ReviewTitle),
            AdditionalOptions = BuildReviewAdditionalOptions(settings)
        };

        return new CreateRunRequest
        {
            Cwd = cwd,
            Prompt = prompt,
            Kind = RunKinds.Review,
            Review = review,
            ApprovalPolicy = string.IsNullOrWhiteSpace(settings.ApprovalPolicy) ? "never" : settings.ApprovalPolicy.Trim(),
            Model = null,
            Sandbox = null
        };
    }

    private static string ResolveReviewPrompt(Settings settings)
    {
        // Syntax: `codex-d http exec review [PROMPT...]`
        var prompt = settings.PromptOption;
        if (string.IsNullOrWhiteSpace(prompt) && settings.Prompt.Length > 1)
        {
            prompt = string.Join(" ", settings.Prompt.Skip(1));
        }

        if (string.Equals(prompt, "-", StringComparison.Ordinal))
        {
            return Console.In.ReadToEnd();
        }

        return string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt;
    }

    private static string ResolvePrompt(Settings settings)
    {
        var prompt = settings.PromptOption;
        if (string.IsNullOrWhiteSpace(prompt) && settings.Prompt.Length > 0)
        {
            prompt = string.Join(" ", settings.Prompt);
        }

        if (string.Equals(prompt, "-", StringComparison.Ordinal))
        {
            return Console.In.ReadToEnd();
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Missing prompt. Provide PROMPT or --prompt.");
        }

        return prompt;
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool UsesReviewOptions(Settings settings) =>
        settings.ReviewUncommitted ||
        !string.IsNullOrWhiteSpace(settings.ReviewBaseBranch) ||
        !string.IsNullOrWhiteSpace(settings.ReviewCommitSha) ||
        !string.IsNullOrWhiteSpace(settings.ReviewTitle) ||
        settings.ReviewArg.Length > 0 ||
        settings.ReviewConfig.Length > 0 ||
        settings.ReviewEnable.Length > 0 ||
        settings.ReviewDisable.Length > 0;

    private static string[] BuildReviewAdditionalOptions(Settings settings)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            args.Add("--model");
            args.Add(settings.Model.Trim());
        }

        foreach (var cfg in settings.ReviewConfig.Select(TrimOrNull).Where(x => x is not null))
        {
            args.Add("--config");
            args.Add(cfg!);
        }

        foreach (var feat in settings.ReviewEnable.Select(TrimOrNull).Where(x => x is not null))
        {
            args.Add("--enable");
            args.Add(feat!);
        }

        foreach (var feat in settings.ReviewDisable.Select(TrimOrNull).Where(x => x is not null))
        {
            args.Add("--disable");
            args.Add(feat!);
        }

        foreach (var raw in settings.ReviewArg.Select(TrimOrNull).Where(x => x is not null))
        {
            args.Add(raw!);
        }

        return args.ToArray();
    }

    internal static async Task<int> StreamAsync(
        RunnerClient client,
        Guid runId,
        bool replay,
        bool follow,
        int? tail,
        bool json,
        CancellationToken cancellationToken)
    {
        var sawCompletion = false;
        var exitCode = 0;

        await foreach (var evt in client.GetEventsAsync(runId, replay, follow, tail, cancellationToken))
        {
            if (json)
            {
                using var doc = JsonDocument.Parse(evt.Data);
                WriteJsonLine(new { eventName = evt.Name, data = doc.RootElement.Clone() });
                continue;
            }

            if (evt.Name == "codex.notification")
            {
                if (TryExtractDelta(evt.Data, out var delta))
                {
                    Console.Out.Write(delta);
                }

                continue;
            }

            if (evt.Name == "run.completed")
            {
                sawCompletion = true;
                if (TryExtractStatus(evt.Data, out var status))
                {
                    Console.Out.WriteLine();
                    AnsiConsole.MarkupLine($"[grey]Completed:[/] {status}");
                    exitCode = status is RunStatuses.Succeeded ? 0 : 1;
                }
                else
                {
                    exitCode = 1;
                }

                continue;
            }
        }

        if (!json)
        {
            Console.Out.Flush();
        }

        return sawCompletion ? exitCode : 0;
    }

    private static bool TryExtractDelta(string json, out string delta)
    {
        delta = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var method = methodEl.GetString();
            if (method is null)
            {
                return false;
            }

            if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            switch (method)
            {
                case "item/agentMessage/delta":
                case "item/commandExecution/outputDelta":
                case "item/fileChange/outputDelta":
                case "item/plan/delta":
                    if (p.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        delta = d.GetString() ?? string.Empty;
                        return delta.Length > 0;
                    }
                    return false;

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractStatus(string json, out string status)
    {
        status = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            status = s.GetString() ?? string.Empty;
            return status.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteJsonLine(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Console.Out.WriteLine(json);
    }
}
