using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts;
using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands;

public sealed class ReviewCommand : AsyncCommand<ReviewCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--mode <MODE>")]
        [DefaultValue("exec")]
        [Description("Review execution mode: 'exec' (codex review) or 'appserver' (codex app-server review/start).")]
        public string Mode { get; init; } = "exec";

        [CommandOption("--delivery <DELIVERY>")]
        [Description("App-server review delivery: 'inline' or 'detached' (only when --mode appserver).")]
        public string? Delivery { get; init; }

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

        [CommandOption("--effort|--reasoning-effort <EFFORT>")]
        [Description("Reasoning effort override (exec-mode only). Examples: none, minimal, low, medium, high, xhigh.")]
        public string? Effort { get; init; }

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
        OutputFormat format;
        try
        {
            format = settings.ResolveOutputFormat(OutputFormatUsage.Streaming);
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

        ResolvedClientSettings resolved;
        try
        {
            resolved = await settings.ResolveAsync(cancellationToken);
        }
        catch (RunnerResolutionFailure ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("runner_not_found", ex.UserMessage);
            }
            else
            {
                Console.Error.WriteLine(ex.UserMessage);
            }
            return 1;
        }

        try
        {
            var prompt = ResolvePrompt(settings);

            var mode = (settings.Mode ?? "exec").Trim();
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = "exec";
            }

            if (!string.Equals(mode, "exec", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "appserver", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Invalid --mode. Use 'exec' or 'appserver'.");
            }

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

            if (string.Equals(mode, "appserver", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(settings.Effort))
                {
                    throw new ArgumentException("--effort/--reasoning-effort is only supported with --mode exec.");
                }

                if (settings.Config.Length > 0 || settings.Enable.Length > 0 || settings.Disable.Length > 0 || settings.Arg.Length > 0)
                {
                    throw new ArgumentException("--config/--enable/--disable/--arg are only supported with --mode exec.");
                }
            }

            var review = new RunReviewRequest
            {
                Mode = mode,
                Delivery = TrimOrNull(settings.Delivery),
                Uncommitted = uncommitted,
                BaseBranch = baseBranch,
                CommitSha = commitSha,
                Title = TrimOrNull(settings.Title),
                AdditionalOptions = string.Equals(mode, "appserver", StringComparison.OrdinalIgnoreCase)
                    ? []
                    : BuildAdditionalOptions(settings)
            };

            var request = new CreateRunRequest
            {
                Cwd = resolved.Cwd,
                Prompt = prompt,
                Kind = RunKinds.Review,
                Review = review,
                Model = TrimOrNull(settings.Model),
                Effort = TrimOrNull(settings.Effort),
                ApprovalPolicy = string.IsNullOrWhiteSpace(settings.ApprovalPolicy) ? "never" : settings.ApprovalPolicy.Trim()
            };

            using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);
            var created = await client.CreateRunAsync(request, cancellationToken);

            var json = format != OutputFormat.Human;
            if (json)
            {
                CliOutput.WriteJsonLine(new { eventName = "run.created", runId = created.RunId, status = created.Status });
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
                format,
                cancellationToken);
        }
        catch (ArgumentException ex)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("invalid_args", ex.Message);
                return 2;
            }

            Console.Error.WriteLine(ex.Message);
            return 2;
        }
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
