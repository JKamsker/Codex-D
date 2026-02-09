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
            var prompt = ResolvePrompt(settings);

            using var client = new RunnerClient(resolved.BaseUrl, resolved.Token);

            var request = CreateExecRequest(settings, resolved.Cwd, prompt);

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

            if (evt.Name == "codex.rollup.outputLine")
            {
                if (TryExtractRollupOutputLine(evt.Data, out var text, out var endsWithNewline, out var isControl) && !isControl)
                {
                    Console.Out.Write(text);
                    if (endsWithNewline)
                    {
                        Console.Out.WriteLine();
                    }
                }

                continue;
            }

            if (evt.Name == "codex.rollup.agentMessage")
            {
                if (TryExtractRollupAgentMessage(evt.Data, out var text))
                {
                    Console.Out.Write(text);
                    if (!text.EndsWith('\n'))
                    {
                        Console.Out.WriteLine();
                    }
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

        if (follow && !sawCompletion && !cancellationToken.IsCancellationRequested)
        {
            if (!json)
            {
                Console.Error.WriteLine("Event stream ended before run.completed was received.");
            }
            return 1;
        }

        return sawCompletion ? exitCode : 0;
    }

    private static bool TryExtractRollupOutputLine(string json, out string text, out bool endsWithNewline, out bool isControl)
    {
        text = string.Empty;
        endsWithNewline = false;
        isControl = false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

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

    private static bool TryExtractRollupAgentMessage(string json, out string text)
    {
        text = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            text = t.GetString() ?? string.Empty;
            return text.Length > 0;
        }
        catch
        {
            return false;
        }
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
