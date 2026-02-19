using System;
using System.IO;
using System.Threading;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts;
using CodexD.HttpRunner.Runs;
using Xunit;

namespace CodexD.Tests;

public sealed class ReviewPromptScopeCompatibilityTests
{
    [Fact]
    public async Task Review_Exec_PromptOnly_DoesNotDefaultUncommitted()
    {
        var exec = new CaptureReviewExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(
            new CreateRunRequest
            {
                Cwd = cwd,
                Prompt = "custom instructions",
                Review = new RunReviewRequest { Mode = "exec" },
                Model = null,
                Effort = null,
                Sandbox = null,
                ApprovalPolicy = "never"
            },
            CancellationToken.None);

        var run = await sdk.GetRunAsync(created.RunId, CancellationToken.None);
        Assert.NotNull(run.Review);
        Assert.Equal("exec", (run.Review!.Mode ?? string.Empty).Trim().ToLowerInvariant());
        Assert.False(run.Review.Uncommitted);
        Assert.True(string.IsNullOrWhiteSpace(run.Review.BaseBranch));
        Assert.True(string.IsNullOrWhiteSpace(run.Review.CommitSha));
    }

    [Fact]
    public async Task Review_Exec_PromptPlusBase_RoutesToAppServer()
    {
        var exec = new CaptureReviewExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(
            new CreateRunRequest
            {
                Cwd = cwd,
                Prompt = "review instructions",
                Review = new RunReviewRequest { Mode = "exec", BaseBranch = "main" },
                Model = null,
                Effort = null,
                Sandbox = null,
                ApprovalPolicy = "never"
            },
            CancellationToken.None);

        var run = await sdk.GetRunAsync(created.RunId, CancellationToken.None);
        Assert.NotNull(run.Review);
        Assert.Equal("appserver", (run.Review!.Mode ?? string.Empty).Trim().ToLowerInvariant());
        Assert.Equal("main", run.Review.BaseBranch);
        Assert.Equal("read-only", (run.Sandbox ?? string.Empty).Trim().ToLowerInvariant());
    }

    [Fact]
    public async Task Review_Exec_PromptPlusUncommitted_RoutesToAppServer()
    {
        var exec = new CaptureReviewExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(
            new CreateRunRequest
            {
                Cwd = cwd,
                Prompt = "review instructions",
                Review = new RunReviewRequest { Mode = "exec", Uncommitted = true },
                Model = null,
                Effort = null,
                Sandbox = null,
                ApprovalPolicy = "never"
            },
            CancellationToken.None);

        var run = await sdk.GetRunAsync(created.RunId, CancellationToken.None);
        Assert.NotNull(run.Review);
        Assert.Equal("appserver", (run.Review!.Mode ?? string.Empty).Trim().ToLowerInvariant());
        Assert.True(run.Review.Uncommitted);
        Assert.Equal("read-only", (run.Sandbox ?? string.Empty).Trim().ToLowerInvariant());
    }

    [Fact]
    public async Task Review_AppServer_PromptOnly_DefaultsUncommitted()
    {
        var exec = new CaptureReviewExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(
            new CreateRunRequest
            {
                Cwd = cwd,
                Prompt = "developer instructions",
                Review = new RunReviewRequest { Mode = "appserver" },
                Model = null,
                Effort = null,
                Sandbox = null,
                ApprovalPolicy = "never"
            },
            CancellationToken.None);

        var run = await sdk.GetRunAsync(created.RunId, CancellationToken.None);
        Assert.NotNull(run.Review);
        Assert.Equal("appserver", (run.Review!.Mode ?? string.Empty).Trim().ToLowerInvariant());
        Assert.True(run.Review.Uncommitted);
        Assert.Equal("read-only", (run.Sandbox ?? string.Empty).Trim().ToLowerInvariant());
    }

    private sealed class CaptureReviewExecutor : IRunExecutor
    {
        public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
        {
            context.SetInterrupt(_ => Task.CompletedTask);
            await context.SetCodexIdsAsync("thread-test", "turn-test", null, ct);
            return new RunExecutionResult { Status = RunStatuses.Succeeded, Error = null };
        }
    }
}
