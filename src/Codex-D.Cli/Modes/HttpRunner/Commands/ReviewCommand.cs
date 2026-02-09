using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands;

public sealed class ReviewCommand : AsyncCommand<ReviewCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("-p|--prompt <PROMPT>")]
        [Description("Optional custom review instructions. Use '-' to read stdin.")]
        public string? PromptOption { get; init; }

        [CommandArgument(0, "[PROMPT]")]
        [Description("Optional custom review instructions. Use '-' to read stdin.")]
        public string[] Prompt { get; init; } = [];

        [CommandOption("-d|--detach")]
        [Description("Detach after creating the review run (does not stream output).")]
        public bool Detach { get; init; }

        [CommandOption("--uncommitted")]
        [Description("Review staged/unstaged/untracked changes.")]
        public bool Uncommitted { get; init; }

        [CommandOption("--base <BRANCH>")]
        [Description("Review changes against base branch.")]
        public string? BaseBranch { get; init; }

        [CommandOption("--commit <SHA>")]
        [Description("Review a specific commit SHA.")]
        public string? CommitSha { get; init; }

        [CommandOption("--title <TITLE>")]
        [Description("Optional review title.")]
        public string? Title { get; init; }

        [CommandOption("--model <MODEL>")]
        [Description("Model the review should use (forwarded to `codex review`).")]
        public string? Model { get; init; }

        [CommandOption("-c|--config <KEY=VALUE>")]
        [Description("Forward a `--config <key=value>` option to `codex review` (repeatable).")]
        public string[] Config { get; init; } = [];

        [CommandOption("--enable <FEATURE>")]
        [Description("Forward an `--enable <feature>` option to `codex review` (repeatable).")]
        public string[] Enable { get; init; } = [];

        [CommandOption("--disable <FEATURE>")]
        [Description("Forward a `--disable <feature>` option to `codex review` (repeatable).")]
        public string[] Disable { get; init; } = [];

        [CommandOption("--arg <ARG>")]
        [Description("Additional raw args forwarded to `codex review` (repeatable).")]
        public string[] Arg { get; init; } = [];

        [CommandOption("--approval-policy <POLICY>")]
        [DefaultValue("never")]
        [Description("Runner approval policy (generally keep this as 'never' for non-interactive review runs).")]
        public string ApprovalPolicy { get; init; } = "never";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var resolved = settings.Resolve();
        var prompt = ResolvePrompt(settings);

        var baseBranch = TrimOrNull(settings.BaseBranch);
        var commitSha = TrimOrNull(settings.CommitSha);
        var uncommitted = settings.Uncommitted;

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
            throw new ArgumentException("Only one of --uncommitted, --base, or --commit can be specified.");
        }

        var review = new RunReviewRequest
        {
            Uncommitted = uncommitted,
            BaseBranch = baseBranch,
            CommitSha = commitSha,
            Title = TrimOrNull(settings.Title),
            AdditionalOptions = BuildAdditionalOptions(settings)
        };

        var request = new CreateRunRequest
        {
            Cwd = resolved.Cwd,
            Prompt = prompt,
            Kind = RunKinds.Review,
            Review = review,
            ApprovalPolicy = string.IsNullOrWhiteSpace(settings.ApprovalPolicy) ? "never" : settings.ApprovalPolicy.Trim()
        };

        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);
        var created = await client.CreateRunAsync(request, cancellationToken);

        if (settings.Json)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                new { eventName = "run.created", runId = created.RunId, status = created.Status },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        }
        else
        {
            AnsiConsole.MarkupLine($"RunId: [cyan]{created.RunId:D}[/]  Status: [grey]{created.Status}[/]");
        }

        if (settings.Detach)
        {
            return 0;
        }

        return await ExecCommand.StreamAsync(
            client,
            created.RunId,
            replay: true,
            follow: true,
            tail: null,
            json: settings.Json,
            cancellationToken);
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

        return string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt;
    }

    private static string[] BuildAdditionalOptions(Settings settings)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            args.Add("--model");
            args.Add(settings.Model.Trim());
        }

        foreach (var cfg in settings.Config.Select(TrimOrNull).Where(x => x is not null))
        {
            args.Add("--config");
            args.Add(cfg!);
        }

        foreach (var feat in settings.Enable.Select(TrimOrNull).Where(x => x is not null))
        {
            args.Add("--enable");
            args.Add(feat!);
        }

        foreach (var feat in settings.Disable.Select(TrimOrNull).Where(x => x is not null))
        {
            args.Add("--disable");
            args.Add(feat!);
        }

        foreach (var raw in settings.Arg.Select(TrimOrNull).Where(x => x is not null))
        {
            args.Add(raw!);
        }

        return args.ToArray();
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

