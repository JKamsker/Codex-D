using System.ComponentModel;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts;
using CodexD.Shared.Output;
using CodexD.Shared.Strings;
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
        [Description("Optional review prompt. In exec-mode this becomes a custom review target (mutually exclusive with --base/--commit/--uncommitted). In appserver-mode it becomes developer instructions (can be combined with scope). Use '-' to read stdin.")]
        public string? PromptOption { get; init; }

        [CommandArgument(0, "[PROMPT]")]
        [Description("Optional review prompt. In exec-mode this becomes a custom review target (mutually exclusive with --base/--commit/--uncommitted). In appserver-mode it becomes developer instructions (can be combined with scope). Use '-' to read stdin.")]
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

        [CommandOption("-r|--reasoning|--effort|--reasoning-effort <EFFORT>")]
        [Description("Reasoning effort override. Examples: none, minimal, low, medium, high, xhigh.")]
        public string? Effort { get; init; }

        [CommandOption("--sandbox <MODE>")]
        [Description("Sandbox mode (primarily affects app-server mode). Examples: read-only, workspace-write, danger-full-access.")]
        public string? Sandbox { get; init; }

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

            var baseBranch = StringHelpers.TrimOrNull(settings.BaseBranch);
            var commitSha = StringHelpers.TrimOrNull(settings.CommitSha);
            var hasPrompt = !string.IsNullOrWhiteSpace(prompt);
            var uncommitted = settings.Uncommitted;

            var explicitTargets = 0;
            if (uncommitted) explicitTargets++;
            if (!string.IsNullOrWhiteSpace(baseBranch)) explicitTargets++;
            if (!string.IsNullOrWhiteSpace(commitSha)) explicitTargets++;

            if (explicitTargets > 1)
            {
                throw new ArgumentException("Only one of --uncommitted, --base, or --commit can be specified.");
            }

            var autoSwitchedToAppServer = false;
            if (string.Equals(mode, "exec", StringComparison.OrdinalIgnoreCase) && hasPrompt && explicitTargets > 0)
            {
                if (settings.Config.Length > 0 || settings.Enable.Length > 0 || settings.Disable.Length > 0 || settings.Arg.Length > 0)
                {
                    throw new ArgumentException(
                        "In exec-mode, `codex review` cannot combine --uncommitted/--base/--commit with a custom PROMPT. " +
                        "Use --mode appserver for prompt+scope reviews (note: --config/--enable/--disable/--arg are exec-only).");
                }

                mode = "appserver";
                autoSwitchedToAppServer = true;
            }

            if (string.Equals(mode, "appserver", StringComparison.OrdinalIgnoreCase))
            {
                if (settings.Config.Length > 0 || settings.Enable.Length > 0 || settings.Disable.Length > 0 || settings.Arg.Length > 0)
                {
                    throw new ArgumentException("--config/--enable/--disable/--arg are only supported with --mode exec.");
                }
            }

            if (explicitTargets == 0)
            {
                // Default review target differs by mode:
                // - exec: when a prompt is provided, upstream codex treats it as the target (custom instructions), so we must not default --uncommitted.
                // - appserver: prompt is developer instructions, so default to --uncommitted when no explicit scope is provided.
                if (!hasPrompt || string.Equals(mode, "appserver", StringComparison.OrdinalIgnoreCase))
                {
                    uncommitted = true;
                }
            }

            var review = new RunReviewRequest
            {
                Mode = mode,
                Delivery = StringHelpers.TrimOrNull(settings.Delivery),
                Uncommitted = uncommitted,
                BaseBranch = baseBranch,
                CommitSha = commitSha,
                Title = StringHelpers.TrimOrNull(settings.Title),
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
                Model = StringHelpers.TrimOrNull(settings.Model),
                Effort = StringHelpers.TrimOrNull(settings.Effort),
                Sandbox = StringHelpers.TrimOrNull(settings.Sandbox),
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
                if (autoSwitchedToAppServer)
                {
                    AnsiConsole.MarkupLine("[yellow]Note:[/] prompt+scope is not supported by `codex review` (exec-mode); running this review via app-server to preserve both.");
                }
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

        foreach (var cfg in settings.Config.Select(StringHelpers.TrimOrNull).Where(x => x is not null))
        {
            args.Add("--config");
            args.Add(cfg!);
        }

        foreach (var feat in settings.Enable.Select(StringHelpers.TrimOrNull).Where(x => x is not null))
        {
            args.Add("--enable");
            args.Add(feat!);
        }

        foreach (var feat in settings.Disable.Select(StringHelpers.TrimOrNull).Where(x => x is not null))
        {
            args.Add("--disable");
            args.Add(feat!);
        }

        foreach (var raw in settings.Arg.Select(StringHelpers.TrimOrNull).Where(x => x is not null))
        {
            args.Add(raw!);
        }

        return args.ToArray();
    }

}
