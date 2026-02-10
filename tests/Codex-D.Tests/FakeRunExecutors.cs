using System.Text.Json;
using CodexD.HttpRunner.Contracts;
using CodexD.HttpRunner.Runs;

namespace CodexD.Tests;

internal sealed class ImmediateSuccessExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);
        await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

        // Use commandExecution output deltas with newlines so the server's rollup writer can persist completed lines.
        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "hello\nwor" }),
            ct);

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

        await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

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

        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "late\n" }),
            ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class MessagesAndThinkingExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);
        await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

        // Thinking summaries (server extracts **...** lines from commandExecution output deltas while "thinking" is active).
        await context.PublishNotificationAsync("item/commandExecution/outputDelta", JsonSerializer.SerializeToElement(new { delta = "thinking" }), ct);
        await context.PublishNotificationAsync("item/commandExecution/outputDelta", JsonSerializer.SerializeToElement(new { delta = "**Phase 1**\nnot a heading\n**Phase 2**\n" }), ct);
        await context.PublishNotificationAsync("item/commandExecution/outputDelta", JsonSerializer.SerializeToElement(new { delta = "final" }), ct);
        await context.PublishNotificationAsync("item/commandExecution/outputDelta", JsonSerializer.SerializeToElement(new { delta = "**ignored**" }), ct);

        // Completed agent messages (server extracts item/completed -> item.type=agentMessage -> item.text).
        await context.PublishNotificationAsync("item/completed", JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "one" } }), ct);
        await context.PublishNotificationAsync("item/completed", JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "two" } }), ct);
        await context.PublishNotificationAsync("item/completed", JsonSerializer.SerializeToElement(new { item = new { type = "agentMessage", text = "three" } }), ct);

        return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
    }
}

internal sealed class PartialLineExecutor : IRunExecutor
{
    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        context.SetInterrupt(_ => Task.CompletedTask);
        await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

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
        await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

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
        await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

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
        await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "a\r" }),
            ct);

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
        await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

        await context.PublishNotificationAsync(
            "item/commandExecution/outputDelta",
            JsonSerializer.SerializeToElement(new { delta = "hello" }),
            ct);

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
        await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

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

        if (attempt == 1)
        {
            var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.SetInterrupt(_ =>
            {
                interrupted.TrySetResult();
                return Task.CompletedTask;
            });

            await context.SetCodexIdsAsync("thread-test", "turn-test", ct);

            await context.PublishNotificationAsync(
                "item/commandExecution/outputDelta",
                JsonSerializer.SerializeToElement(new { delta = "phase1\n" }),
                ct);

            FirstStarted.TrySetResult();

            await interrupted.Task.WaitAsync(ct);
            return new RunExecutionResult { Status = RunStatuses.Interrupted, Error = null };
        }

        context.SetInterrupt(_ => Task.CompletedTask);
        await context.SetCodexIdsAsync("thread-test", $"turn-test-{attempt}", ct);

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
