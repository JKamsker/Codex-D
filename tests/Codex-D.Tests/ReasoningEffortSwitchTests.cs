using System;
using System.IO;
using System.Threading;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts;
using CodexD.HttpRunner.Runs;
using Xunit;

namespace CodexD.Tests;

public sealed class ReasoningEffortSwitchTests
{
    [Fact]
    public async Task Runs_Create_PersistsEffort_AndPassesToExecutor()
    {
        var exec = new CaptureEffortExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(
            new CreateRunRequest
            {
                Cwd = cwd,
                Prompt = "hi",
                Effort = "high",
                Model = null,
                Sandbox = null,
                ApprovalPolicy = "never"
            },
            CancellationToken.None);

        var seen = await exec.SeenEffort.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("high", seen);

        var run = await sdk.GetRunAsync(created.RunId, CancellationToken.None);
        Assert.Equal("high", run.Effort);
    }

    [Fact]
    public async Task Resume_UpdatesEffort_WhenProvided()
    {
        var exec = new StopThenCaptureEffortExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(
            new CreateRunRequest
            {
                Cwd = cwd,
                Prompt = "hi",
                Effort = "low",
                Model = null,
                Sandbox = null,
                ApprovalPolicy = "never"
            },
            CancellationToken.None);

        await exec.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await sdk.StopAsync(created.RunId, CancellationToken.None);
        await WaitForStatusAsync(sdk, created.RunId, RunStatuses.Paused, TimeSpan.FromSeconds(5));

        await sdk.ResumeAsync(created.RunId, prompt: "continue", effort: "high", ct: CancellationToken.None);

        var seen = await exec.SecondEffort.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("high", seen);

        var run = await sdk.GetRunAsync(created.RunId, CancellationToken.None);
        Assert.Equal("high", run.Effort);
    }

    private static async Task WaitForStatusAsync(RunnerClient sdk, Guid runId, string status, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var run = await sdk.GetRunAsync(runId, cts.Token);
                if (string.Equals(run.Status, status, StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(50, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for run {runId:D} to reach status '{status}'.");
        }
    }

    private sealed class CaptureEffortExecutor : IRunExecutor
    {
        public TaskCompletionSource<string?> SeenEffort { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
        {
            context.SetInterrupt(_ => Task.CompletedTask);
            SeenEffort.TrySetResult(context.Effort);

            await context.SetCodexIdsAsync("thread-test", "turn-test", null, ct);
            return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
        }
    }

    private sealed class StopThenCaptureEffortExecutor : IRunExecutor
    {
        private int _attempt;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> SecondEffort { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _attempt);

            if (attempt == 1)
            {
                var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                context.SetInterrupt(_ =>
                {
                    interrupted.TrySetResult();
                    return Task.CompletedTask;
                });

                await context.SetCodexIdsAsync("thread-test", "turn-test", null, ct);

                FirstStarted.TrySetResult();

                await interrupted.Task.WaitAsync(ct);
                return new RunExecutionResult { Status = RunStatuses.Interrupted, Error = null };
            }

            context.SetInterrupt(_ => Task.CompletedTask);
            SecondEffort.TrySetResult(context.Effort);
            await context.SetCodexIdsAsync("thread-test", "turn-test-2", null, ct);

            return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
        }
    }
}
