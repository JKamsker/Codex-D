using System.ComponentModel;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts;
using CodexD.Shared.Output;
using CodexD.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Run;

public sealed class RunThinkingCommand : AsyncCommand<RunThinkingCommand.Settings>
{
    public sealed class Settings : ClientSettingsBase
    {
        [CommandOption("--tail-events <N>")]
        [Description("Only scan the last N events on the server (performance guard). Default: 20000.")]
        public int? TailEvents { get; init; }

        [CommandOption("--follow")]
        [Description("Keep following run events and print new thinking summaries as they appear (human output only).")]
        public bool Follow { get; init; }

        [CommandOption("--last")]
        [Description("Use the most recent run for the current --cd/cwd.")]
        public bool Last { get; init; }

        [CommandArgument(0, "[RUN_ID]")]
        public string? RunId { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        OutputFormat format;
        try
        {
            format = settings.ResolveOutputFormat(OutputFormatUsage.Single);
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

        if (settings.Follow && format != OutputFormat.Human)
        {
            CliOutput.WriteJsonError("invalid_args", "--follow is only supported with human output.");
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

        using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

        Guid runId;
        if (settings.Last)
        {
            var runs = await client.ListRunsAsync(resolved.Cwd, all: false, cancellationToken);
            var latest = runs.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (latest is null)
            {
                if (format != OutputFormat.Human)
                {
                    CliOutput.WriteJsonError("no_runs", "No runs found for this directory.");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]No runs found for this directory.[/]");
                }
                return 1;
            }

            runId = latest.RunId;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.RunId) || !Guid.TryParse(settings.RunId, out runId))
            {
                if (format != OutputFormat.Human)
                {
                    CliOutput.WriteJsonError("invalid_run_id", "Missing or invalid RUN_ID.");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Missing or invalid RUN_ID.[/]");
                }
                return 2;
            }
        }

        if (format != OutputFormat.Human)
        {
            IReadOnlyList<string> summaries;
            try
            {
                summaries = await client.GetRunThinkingSummariesAsync(runId, settings.TailEvents, cancellationToken);
            }
            catch (Exception ex)
            {
                CliOutput.WriteJsonError("fetch_failed", ex.Message ?? string.Empty);
                return 1;
            }

            CliOutput.WriteJsonLine(new { runId, items = summaries });
            return 0;
        }

        IReadOnlyList<ThinkingSummaryItem> summaryItems;
        try
        {
            summaryItems = await client.GetRunThinkingSummaryItemsAsync(runId, settings.TailEvents, cancellationToken);
        }
        catch
        {
            summaryItems = Array.Empty<ThinkingSummaryItem>();
        }

        if (summaryItems.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No thinking summaries found for:[/] {runId:D}");
            if (!settings.Follow)
            {
                return 0;
            }
        }

        var last = string.Empty;
        foreach (var item in summaryItems)
        {
            var fixedText = TextMojibakeRepair.Fix(item.Text);
            Console.Out.WriteLine($"{FormatTimestamp(item.CreatedAt)} {fixedText}");
            last = fixedText;
        }

        if (!settings.Follow)
        {
            return 0;
        }

        var inThinking = summaryItems.Count > 0;

        await foreach (var evt in client.GetEventsAsync(runId, replay: false, follow: true, tail: null, cancellationToken))
        {
            if (evt.Name is "run.completed" or "run.paused")
            {
                break;
            }

            if (evt.Name == "codex.notification")
            {
                if (TryExtractOutputDelta(evt.Data, out var createdAt, out var delta))
                {
                    AddSummariesFromDelta(createdAt, delta, ref last, ref inThinking);
                }

                continue;
            }

            if (evt.Name == "codex.rollup.outputLine")
            {
                if (TryExtractRollupOutputLine(evt.Data, out var createdAt, out var text, out var endsWithNewline, out var isControl))
                {
                    if (isControl)
                    {
                        var t = text.Trim();
                        if (string.Equals(t, "thinking", StringComparison.OrdinalIgnoreCase))
                        {
                            inThinking = true;
                        }
                        else if (string.Equals(t, "final", StringComparison.OrdinalIgnoreCase))
                        {
                            inThinking = false;
                        }
                    }
                    else
                    {
                        var delta = endsWithNewline ? text + "\n" : text;
                        AddSummariesFromDelta(createdAt, delta, ref last, ref inThinking);
                    }
                }
            }
        }

        return 0;
    }

    private static bool TryExtractOutputDelta(string json, out DateTimeOffset createdAt, out string delta)
    {
        createdAt = DateTimeOffset.UtcNow;
        delta = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("createdAt", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.String)
            {
                var raw = createdAtEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, out var parsed))
                {
                    createdAt = parsed;
                }
            }

            if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!string.Equals(methodEl.GetString(), "item/commandExecution/outputDelta", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (p.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
            {
                delta = d.GetString() ?? string.Empty;
                return delta.Length > 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractRollupOutputLine(string json, out DateTimeOffset createdAt, out string text, out bool endsWithNewline, out bool isControl)
    {
        createdAt = DateTimeOffset.UtcNow;
        text = string.Empty;
        endsWithNewline = false;
        isControl = false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("createdAt", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.String)
            {
                var raw = createdAtEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, out var parsed))
                {
                    createdAt = parsed;
                }
            }

            if (root.TryGetProperty("isControl", out var ctrlEl) && ctrlEl.ValueKind == JsonValueKind.True)
            {
                isControl = true;
            }

            if (root.TryGetProperty("endsWithNewline", out var nlEl) && nlEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                endsWithNewline = nlEl.GetBoolean();
            }

            if (!root.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            text = t.GetString() ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddSummariesFromDelta(DateTimeOffset createdAt, string delta, ref string last, ref bool inThinking)
    {
        var trimmed = delta.Trim();
        if (string.Equals(trimmed, "thinking", StringComparison.OrdinalIgnoreCase))
        {
            inThinking = true;
            return;
        }

        if (string.Equals(trimmed, "final", StringComparison.OrdinalIgnoreCase))
        {
            inThinking = false;
            return;
        }

        var maybeThinking = inThinking || delta.Contains("thinking", StringComparison.OrdinalIgnoreCase);
        if (!maybeThinking)
        {
            return;
        }

        foreach (var rawLine in delta.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = rawLine.Trim();
            if (!t.StartsWith("**", StringComparison.Ordinal) || !t.EndsWith("**", StringComparison.Ordinal) || t.Length <= 4)
            {
                continue;
            }

            var summary = t[2..^2].Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            summary = TextMojibakeRepair.Fix(summary);

            if (string.Equals(summary, last, StringComparison.Ordinal))
            {
                continue;
            }

            Console.Out.WriteLine($"{FormatTimestamp(createdAt)} {summary}");
            last = summary;
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
