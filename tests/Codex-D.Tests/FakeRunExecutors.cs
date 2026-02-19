using System.Text.Json;
using System.Text;
using CodexD.HttpRunner.Contracts;
using CodexD.HttpRunner.Runs;

namespace CodexD.Tests;

internal sealed class ImmediateSuccessExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        // Use commandExecution output deltas with newlines so the server's rollup writer can persist completed lines.
        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "hello\nwor");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "hello\nwor" }),
            ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "ld\ndone\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "ld\ndone\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class CoordinatedExecutor : IRunExecutor
{
    public TaskCompletionSource FirstPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ =>
        {
            Interrupted.TrySetResult();
            return Task.CompletedTask;
        });

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "early\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "early\n" }),
            ct);
        FirstPublished.TrySetResult();

        await Task.WhenAny(AllowContinue.Task, Interrupted.Task);

        if (Interrupted.Task.IsCompleted)
        {
            return new RunExecutionResult { Status = RunStatuses.Interrupted, Error = null };
        }

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "late\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "late\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class NonInterruptibleBlockingExecutor : IRunExecutor
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        // Intentionally do NOT call context.SetInterrupt(...).
        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        Started.TrySetResult();

        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class InterruptThrowsBlockingExecutor : IRunExecutor
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => throw new InvalidOperationException("boom"));

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        Started.TrySetResult();

        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class MissingRolloutPathWithSiblingExecutor : IRunExecutor
{
    public string? ExpectedRolloutPath { get; private set; }
    public string? ActualRolloutPath { get; private set; }

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var dir = Path.Combine(context.Cwd, "rollouts");
        Directory.CreateDirectory(dir);

        const string ts = "2026-02-19T00-00-00";
        ExpectedRolloutPath = Path.Combine(dir, $"rollout-{ts}-expected.jsonl");
        ActualRolloutPath = Path.Combine(dir, $"rollout-{ts}-actual.jsonl");

        // Create a sibling rollout file matching the same timestamp prefix, but do not create the expected path.
        File.WriteAllText(ActualRolloutPath, "{\"ok\":true}\n");

        await context.SetCodexIdsAsync("thread-test", "turn-test", ExpectedRolloutPath, ct);

        // Publish a notification to trigger the runner's rollout path resolution logic.
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "hi\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class MessagesAndThinkingExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        // Thinking summaries (server extracts **...** lines from commandExecution output deltas while "thinking" is active).
        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "thinking");
        await context.PublishNotificationAsync("item/commandExecution/outputDelta", JsonSerializer.SerializeToElement(new { delta = "thinking" }), ct);
        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "**Phase 1**\nnot a heading\n**Phase 2**\n");
        await context.PublishNotificationAsync("item/commandExecution/outputDelta", JsonSerializer.SerializeToElement(new { delta = "**Phase 1**\nnot a heading\n**Phase 2**\n" }), ct);
        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "final");
        await context.PublishNotificationAsync("item/commandExecution/outputDelta", JsonSerializer.SerializeToElement(new { delta = "final" }), ct);
        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "**ignored**");
        await context.PublishNotificationAsync("item/commandExecution/outputDelta", JsonSerializer.SerializeToElement(new { delta = "**ignored**" }), ct);

        // Completed agent messages (server extracts item/completed -> item.type=agentMessage -> item.text).
        TestCodexRollout.AppendAgentMessage(rolloutPath, "one");
        await context.PublishNotificationAsync("item/completed", JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "one" } }), ct);
        TestCodexRollout.AppendAgentMessage(rolloutPath, "two");
        await context.PublishNotificationAsync("item/completed", JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "two" } }), ct);
        TestCodexRollout.AppendAgentMessage(rolloutPath, "three");
        await context.PublishNotificationAsync("item/completed", JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "three" } }), ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class MessagesAndThinkingNoRolloutExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "thinking" }),
            ct);

        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "**Phase 1**\nnot a heading\n**Phase 2**\n" }),
            ct);

        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "final" }),
            ct);

        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "**ignored**" }),
            ct);

        await context.PublishNotificationAsync(
            "item/completed",
            JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "one" } }),
            ct);

        await context.PublishNotificationAsync(
            "item/completed",
            JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "two" } }),
            ct);

        await context.PublishNotificationAsync(
            "item/completed",
            JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "three" } }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class PartialRolloutMaterializationExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        // Persist some history into the rollout file.
        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "thinking");
        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "**Phase 1**\n");
        TestCodexRollout.AppendAgentMessage(rolloutPath, "one");
        TestCodexRollout.AppendAgentMessage(rolloutPath, "two");

        // Publish newer data only via notifications (simulates not-yet-materialized rollout tail).
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "**Phase 2**\n" }),
            ct);

        await context.PublishNotificationAsync(
            "item/completed",
            JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "three" } }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class RolloutOnlyMessagesExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        TestCodexRollout.AppendAgentMessage(rolloutPath, "one");
        TestCodexRollout.AppendAgentMessage(rolloutPath, "two");
        TestCodexRollout.AppendAgentMessage(rolloutPath, "three");

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class PartialLineExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "partial");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "partial" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class CrLfExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "a\r\nb\r\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "a\r\nb\r\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class PlanOnlyExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        await context.PublishNotificationAsync(
            "item/plan/delta",
            JsonSerializer.SerializeToElement(new { delta = "plan line\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class SplitCrLfAcrossDeltasExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "a\r");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "a\r" }),
            ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "\nb\r\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "\nb\r\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class NewlineOnlyDeltaExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "hello");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "hello" }),
            ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class InlineThinkingMarkersExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);

        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);
        await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "thinking\n**Phase 1**\nfinal\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "thinking\n**Phase 1**\nfinal\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class StopThenSucceedExecutor : IRunExecutor
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, int> _attempts = new();

    public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        var attempt = IncrementAttempt(context.RunId);
        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);

        if (attempt == 1)
        {
            var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.SetInterrupt(_ =>
            {
                interrupted.TrySetResult();
                return Task.CompletedTask;
            });

            await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

            TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "phase1\n");
            await context.PublishNotificationAsync(
                "item/commandExecution/outputDelta",
                JsonSerializer.SerializeToElement(new { delta = "phase1\n" }),
                ct);

            FirstStarted.TrySetResult();

            await interrupted.Task.WaitAsync(ct);
            return new RunExecutionResult { Status = RunStatuses.Interrupted, Error = null };
        }

        context.SetInterrupt(_ => Task.CompletedTask);
        await context.SetCodexIdsAsync("thread-test", $"turn-test-{attempt}", rolloutPath, ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "phase2\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "phase2\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }

    private int IncrementAttempt(Guid runId)
    {
        lock (_lock)
        {
            if (!_attempts.TryGetValue(runId, out var current))
            {
                current = 0;
            }

            current++;
            _attempts[runId] = current;
            return current;
        }
    }
}

internal sealed class StopThenCaptureContinuationExecutor : IRunExecutor
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, int> _attempts = new();

    public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? SecondKind { get; private set; }
    public string? SecondPrompt { get; private set; }

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        var attempt = IncrementAttempt(context.RunId);
        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);

        if (attempt == 1)
        {
            var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.SetInterrupt(_ =>
            {
                interrupted.TrySetResult();
                return Task.CompletedTask;
            });

            await context.SetCodexIdsAsync("019c75dd-71bb-7e60-9091-227568dcc8fa", "turn-test", rolloutPath, ct);

            TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "phase1\n");
            await context.PublishNotificationAsync(
                "item/commandExecution/outputDelta",
                JsonSerializer.SerializeToElement(new { delta = "phase1\n" }),
                ct);

            FirstStarted.TrySetResult();

            await interrupted.Task.WaitAsync(ct);
            return new RunExecutionResult { Status = RunStatuses.Interrupted, Error = null };
        }

        context.SetInterrupt(_ => Task.CompletedTask);
        await context.SetCodexIdsAsync("019c75dd-71bb-7e60-9091-227568dcc8fa", $"turn-test-{attempt}", rolloutPath, ct);

        SecondKind = RunKinds.Normalize(context.Kind);
        SecondPrompt = context.Prompt;
        SecondStarted.TrySetResult();

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "phase2\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "phase2\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }

    private int IncrementAttempt(Guid runId)
    {
        lock (_lock)
        {
            if (!_attempts.TryGetValue(runId, out var current))
            {
                current = 0;
            }

            current++;
            _attempts[runId] = current;
            return current;
        }
    }
}

internal sealed class StopThenBlockThenSucceedExecutor : IRunExecutor
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, int> _attempts = new();

    public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        var attempt = IncrementAttempt(context.RunId);
        var rolloutPath = TestCodexRollout.EnsureInitialized(context.Cwd);

        if (attempt == 1)
        {
            var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.SetInterrupt(_ =>
            {
                interrupted.TrySetResult();
                return Task.CompletedTask;
            });

            await context.SetCodexIdsAsync("thread-test", "turn-test", rolloutPath, ct);

            TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "phase1\n");
            await context.PublishNotificationAsync(
                "item/commandExecution/outputDelta",
                JsonSerializer.SerializeToElement(new { delta = "phase1\n" }),
                ct);

            FirstStarted.TrySetResult();

            await interrupted.Task.WaitAsync(ct);
            return new RunExecutionResult { Status = RunStatuses.Interrupted, Error = null };
        }

        context.SetInterrupt(_ => Task.CompletedTask);
        await context.SetCodexIdsAsync("thread-test", $"turn-test-{attempt}", rolloutPath, ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "phase2\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "phase2\n" }),
            ct);

        SecondStarted.TrySetResult();

        await AllowFinish.Task.WaitAsync(ct);

        TestCodexRollout.AppendExecCommandOutputDelta(rolloutPath, "done\n");
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "done\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }

    private int IncrementAttempt(Guid runId)
    {
        lock (_lock)
        {
            if (!_attempts.TryGetValue(runId, out var current))
            {
                current = 0;
            }

            current++;
            _attempts[runId] = current;
            return current;
        }
    }
}

internal static class TestCodexRollout
{
    public static string EnsureInitialized(string cwd)
    {
        var path = Path.Combine(cwd, "test-rollout.jsonl");
        if (File.Exists(path))
        {
            return path;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AppendLine(path, new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            type = "session_meta",
            payload = new
            {
                id = "thread-test",
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                cwd,
                originator = "codex-d-tests",
                cli_version = "0.0.0",
                source = "cli",
                model_provider = "test"
            }
        });

        return path;
    }

    public static void AppendExecCommandOutputDelta(string rolloutPath, string delta)
    {
        var chunk = Convert.ToBase64String(Encoding.UTF8.GetBytes(delta ?? string.Empty));
        AppendLine(rolloutPath, new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "exec_command_output_delta",
                call_id = "call-test",
                stream = "stdout",
                chunk
            }
        });
    }

    public static void AppendAgentMessage(string rolloutPath, string message)
    {
        AppendLine(rolloutPath, new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "agent_message",
                message = message ?? string.Empty
            }
        });
    }

    private static void AppendLine(string path, object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.AppendAllText(path, json + "\n", Encoding.UTF8);
    }
}
