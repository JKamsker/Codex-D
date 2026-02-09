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
