using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts;
using CodexD.Shared.Paths;
using Xunit;

namespace CodexD.Tests;

public sealed class RunnerHttpServerTests
{
    [Fact]
    public async Task Health_ReturnsOk_WhenRuntimeDisabled()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor());
        using var client = host.CreateHttpClient(includeToken: false);

        var res = await client.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("disabled", json.GetProperty("codexRuntime").GetString());
    }

    [Fact]
    public async Task Info_ReturnsRunnerMetadata()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor());
        using var client = host.CreateHttpClient(includeToken: false);

        var json = await client.GetFromJsonAsync<JsonElement>("/v1/info");
        Assert.Equal(host.Identity.RunnerId, json.GetProperty("runnerId").GetGuid());
        Assert.Equal(host.Port, json.GetProperty("port").GetInt32());
        Assert.Equal(host.BaseUrl, json.GetProperty("baseUrl").GetString());
    }

    [Fact]
    public async Task Auth_RejectsMissingToken_WhenRequired()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: true, new ImmediateSuccessExecutor());
        using var client = host.CreateHttpClient(includeToken: false);

        var res = await client.GetAsync("/v1/info");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Auth_AllowsToken_WhenRequired()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: true, new ImmediateSuccessExecutor());
        using var client = host.CreateHttpClient(includeToken: true);

        var res = await client.GetAsync("/v1/info");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Runs_Create_And_List_FiltersByExactCwd()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwdA = Path.Combine(host.StateDir, "a");
        var cwdB = Path.Combine(host.StateDir, "b");
        Directory.CreateDirectory(cwdA);
        Directory.CreateDirectory(cwdB);

        await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwdA, Prompt = "x", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwdB, Prompt = "y", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);

        var listA = await sdk.ListRunsAsync(cwdA, all: false, CancellationToken.None);
        Assert.Single(listA);
        Assert.Equal(PathPolicy.TrimTrailingSeparators(Path.GetFullPath(cwdA)), listA[0].Cwd);

        var listAll = await sdk.ListRunsAsync(cwd: null, all: true, CancellationToken.None);
        Assert.True(listAll.Count >= 2);
    }

    [Fact]
    public async Task Sse_ReplayThenFollow_StreamsNotifications_AndCompletes()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        var events = new List<SseEvent>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: true, tail: null, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Contains(events, e => e.Name == "run.meta");
        Assert.Contains(events, e => e.Name == "codex.notification");
        Assert.Contains(events, e => e.Name == "run.completed");
    }

    [Fact]
    public async Task Sse_FollowOnly_DoesNotReplay_OldNotifications()
    {
        var exec = new CoordinatedExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        await exec.FirstPublished.Task; // ensure early event persisted before we attach

        var seen = new List<SseEvent>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumeTask = Task.Run(async () =>
        {
            await foreach (var e in sdk.GetEventsAsync(runId, replay: false, follow: true, tail: null, cts.Token))
            {
                seen.Add(e);
                if (e.Name == "run.meta")
                {
                    attached.TrySetResult();
                }
                if (e.Name == "run.completed")
                {
                    break;
                }
            }
        }, cts.Token);

        await Task.WhenAny(attached.Task, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
        Assert.True(attached.Task.IsCompleted);

        exec.AllowContinue.TrySetResult();
        await consumeTask;

        var notificationPayloads = seen
            .Where(e => e.Name == "codex.notification")
            .Select(e => e.Data)
            .ToList();

        Assert.DoesNotContain(notificationPayloads, p => p.Contains("early", StringComparison.Ordinal));
        Assert.Contains(notificationPayloads, p => p.Contains("late", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sse_Tail_ReplaysOnlyLastNNotifications()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var notif = new List<string>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: 2, CancellationToken.None))
        {
            if (e.Name == "codex.notification")
            {
                notif.Add(e.Data);
            }
        }

        Assert.Single(notif);
        Assert.Contains("world", notif[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interrupt_Endpoint_CallsExecutorInterrupt()
    {
        var exec = new CoordinatedExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await exec.FirstPublished.Task;

        await sdk.InterruptAsync(runId, CancellationToken.None);

        await Task.WhenAny(exec.Interrupted.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(exec.Interrupted.Task.IsCompleted);
    }

    [Fact]
    public async Task RunMessages_ReturnsLastCompletedAgentMessages()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new MessagesAndThinkingExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);
        using var http = host.CreateHttpClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var json = await http.GetFromJsonAsync<JsonElement>($"/v1/runs/{runId:D}/messages?count=2");
        var items = json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("two", items[0].GetProperty("text").GetString());
        Assert.Equal("three", items[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ThinkingSummaries_ReturnsHeadingsWithinThinkingBlocks()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new MessagesAndThinkingExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);
        using var http = host.CreateHttpClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var json = await http.GetFromJsonAsync<JsonElement>($"/v1/runs/{runId:D}/thinking-summaries");
        var items = json.GetProperty("items").EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToList();

        Assert.Contains("Phase 1", items);
        Assert.Contains("Phase 2", items);
        Assert.DoesNotContain("ignored", items);
    }

    private static async Task WaitForTerminalAsync(RunnerClient client, Guid runId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (true)
        {
            var record = await client.GetRunAsync(runId, cts.Token);
            if (record.Status is RunStatuses.Succeeded or RunStatuses.Failed or RunStatuses.Interrupted)
            {
                return;
            }

            await Task.Delay(20, cts.Token);
        }
    }
}
