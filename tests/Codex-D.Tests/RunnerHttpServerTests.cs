using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Contracts;
using CodexD.HttpRunner.Runs;
using CodexD.Shared.Paths;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task Runs_Create_ReturnsBadRequest_WhenCwdDoesNotExist()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor());
        using var http = host.CreateHttpClient(includeToken: false);

        var missingCwd = Path.Combine(host.StateDir, "missing");
        var res = await http.PostAsJsonAsync(
            "/v1/runs",
            new CreateRunRequest { Cwd = missingCwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", json.GetProperty("error").GetString());
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
        Assert.Contains(events, e => e.Name is "codex.rollup.outputLine" or "codex.notification");
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
    public async Task Stop_PausesRun_AndEmitsRunPaused()
    {
        var exec = new CoordinatedExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        await exec.FirstPublished.Task;

        await sdk.StopAsync(runId, CancellationToken.None);

        await WaitForEventAsync(sdk, runId, "run.paused", TimeSpan.FromSeconds(5));

        var run = await sdk.GetRunAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatuses.Paused, run.Status);
    }

    [Fact]
    public async Task Resume_RestartsExecution_AndCompletes()
    {
        var exec = new StopThenSucceedExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        await exec.FirstStarted.Task;

        await sdk.StopAsync(runId, CancellationToken.None);
        await WaitForEventAsync(sdk, runId, "run.paused", TimeSpan.FromSeconds(5));

        await sdk.ResumeAsync(runId, prompt: "continue", CancellationToken.None);
        await WaitForEventAsync(sdk, runId, "run.completed", TimeSpan.FromSeconds(5));

        var completed = await sdk.GetRunAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, completed.Status);
    }

    [Fact]
    public async Task Sse_RawReplay_DoesNotTreatHistoricalRunPausedAsTerminalAfterResume()
    {
        var exec = new StopThenBlockThenSucceedExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec, persistRawEvents: true);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        await exec.FirstStarted.Task;

        await sdk.StopAsync(runId, CancellationToken.None);
        await WaitForEventAsync(sdk, runId, "run.paused", TimeSpan.FromSeconds(5));

        await sdk.ResumeAsync(runId, prompt: "continue", CancellationToken.None);
        await exec.SecondStarted.Task;

        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCompleted = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var consumeTask = Task.Run(async () =>
        {
            await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: true, tail: null, cts.Token))
            {
                if (e.Name == "run.meta")
                {
                    attached.TrySetResult();
                }

                if (e.Name == "run.completed")
                {
                    sawCompleted = true;
                    break;
                }
            }
        }, cts.Token);

        await Task.WhenAny(attached.Task, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
        Assert.True(attached.Task.IsCompleted);

        exec.AllowFinish.TrySetResult();

        await consumeTask;
        Assert.True(sawCompleted);
    }

    [Fact]
    public async Task Sse_Tail_RejectsInvalidValues()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);
        using var http = host.CreateHttpClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        var resZero = await http.GetAsync($"/v1/runs/{runId:D}/events?replay=false&follow=false&tail=0");
        Assert.Equal(HttpStatusCode.BadRequest, resZero.StatusCode);

        var resNegative = await http.GetAsync($"/v1/runs/{runId:D}/events?replay=false&follow=false&tail=-1");
        Assert.Equal(HttpStatusCode.BadRequest, resNegative.StatusCode);

        var resInvalid = await http.GetAsync($"/v1/runs/{runId:D}/events?replay=false&follow=false&tail=abc");
        Assert.Equal(HttpStatusCode.BadRequest, resInvalid.StatusCode);
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

        var lines = new List<string>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: 2, CancellationToken.None))
        {
            if (e.Name == "codex.rollup.outputLine")
            {
                using var doc = JsonDocument.Parse(e.Data);
                if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    lines.Add(t.GetString() ?? string.Empty);
                }
            }
        }

        Assert.Equal(2, lines.Count);
        Assert.Equal("world", lines[0]);
        Assert.Equal("done", lines[1]);
    }

    [Fact]
    public async Task Rollup_PersistsCompletedLines_AndFlushesPartialLineOnCompletion()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new PartialLineExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var store = host.App.Services.GetRequiredService<RunStore>();
        var dir = await store.TryResolveRunDirectoryAsync(runId, CancellationToken.None);
        Assert.NotNull(dir);
        Assert.True(File.Exists(Path.Combine(dir!, "rollup.jsonl")));
        Assert.False(File.Exists(Path.Combine(dir!, "events.jsonl"))); // default is rollup-only

        var rollup = await store.ReadRollupAsync(runId, tail: null, CancellationToken.None);
        Assert.Contains(rollup, r => r.Type == "outputLine" && r.Text == "partial" && r.EndsWithNewline == false);
    }

    [Fact]
    public async Task Rollup_SplitsCrLfIntoDistinctOutputLines()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new CrLfExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var store = host.App.Services.GetRequiredService<RunStore>();
        var rollup = await store.ReadRollupAsync(runId, tail: null, CancellationToken.None);

        var lines = rollup
            .Where(r => r.Type == "outputLine" && r.IsControl != true)
            .Select(r => r.Text)
            .ToList();

        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public async Task Rollup_DoesNotEmitEmptyLine_WhenCrLfIsSplitAcrossDeltaBoundaries()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new SplitCrLfAcrossDeltasExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var store = host.App.Services.GetRequiredService<RunStore>();
        var rollup = await store.ReadRollupAsync(runId, tail: null, CancellationToken.None);

        var lines = rollup
            .Where(r => r.Type == "outputLine" && r.IsControl != true)
            .Select(r => r.Text)
            .ToList();

        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public async Task Rollup_TreatsNewlineOnlyDeltas_AsLineTerminators()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new NewlineOnlyDeltaExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var store = host.App.Services.GetRequiredService<RunStore>();
        var rollup = await store.ReadRollupAsync(runId, tail: null, CancellationToken.None);

        var outputLines = rollup.Where(r => r.Type == "outputLine" && r.IsControl != true).ToList();
        Assert.Single(outputLines);
        Assert.Equal("hello", outputLines[0].Text);
        Assert.True(outputLines[0].EndsWithNewline);
    }

    [Fact]
    public async Task ThinkingSummaries_Works_WhenThinkingMarkersAreInTheSameDelta()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new InlineThinkingMarkersExecutor());
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
    }

    [Fact]
    public async Task Rollup_PersistsThinkingAndFinalMarkers_AsControlOutputLines()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new MessagesAndThinkingExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var store = host.App.Services.GetRequiredService<RunStore>();
        var rollup = await store.ReadRollupAsync(runId, tail: null, CancellationToken.None);

        Assert.Contains(rollup, r => r.Type == "outputLine" && r.IsControl == true && string.Equals(r.Text, "thinking", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rollup, r => r.Type == "outputLine" && r.IsControl == true && string.Equals(r.Text, "final", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rollup, r => r.Type == "outputLine" && r.IsControl != true && r.Text == "**Phase 1**");
        Assert.Contains(rollup, r => r.Type == "outputLine" && r.IsControl != true && r.Text == "**Phase 2**");
    }

    [Fact]
    public async Task Rollup_DoesNotPersistPlanDeltas_WhenNoSupportedNotificationsExist()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new PlanOnlyExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var store = host.App.Services.GetRequiredService<RunStore>();
        var dir = await store.TryResolveRunDirectoryAsync(runId, CancellationToken.None);
        Assert.NotNull(dir);

        Assert.False(File.Exists(Path.Combine(dir!, "rollup.jsonl")));
    }

    [Fact]
    public async Task Sse_ReplayRollupThenFollow_StreamsBothRollupAndDifferentialEvents()
    {
        var exec = new CoordinatedExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        await exec.FirstPublished.Task; // ensure we have replayable rollup content before attaching

        var seen = new List<SseEvent>();
        var sawReplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var consumeTask = Task.Run(async () =>
        {
            await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: true, tail: null, cts.Token))
            {
                seen.Add(e);
                if (e.Name == "codex.rollup.outputLine")
                {
                    sawReplay.TrySetResult();
                }

                if (e.Name == "run.completed")
                {
                    break;
                }
            }
        }, cts.Token);

        await Task.WhenAny(sawReplay.Task, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
        Assert.True(sawReplay.Task.IsCompleted);

        exec.AllowContinue.TrySetResult();
        await consumeTask;

        Assert.Contains(seen, e => e.Name == "codex.rollup.outputLine");
        Assert.Contains(seen, e => e.Name == "codex.notification");
        Assert.Contains(seen, e => e.Name == "run.completed");
    }

    [Fact]
    public async Task Sse_RawReplay_DoesNotDropEventsWithSameCreatedAtAsReplayMax()
    {
        var exec = new CoordinatedExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec, persistRawEvents: true);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        await exec.FirstPublished.Task; // ensure early event persisted before attaching

        var store = host.App.Services.GetRequiredService<RunStore>();
        var raw = await store.ReadRawEventsAsync(runId, tail: null, CancellationToken.None);
        var replayMax = raw.Where(e => e.Type == "codex.notification").Max(e => e.CreatedAt);

        var broadcaster = host.App.Services.GetRequiredService<RunEventBroadcaster>();
        var injected = new RunEventEnvelope
        {
            Type = "codex.notification",
            CreatedAt = replayMax,
            Data = JsonSerializer.SerializeToElement(new
            {
                method = "item/commandExecution/outputDelta",
                @params = new { delta = "injected\n" }
            })
        };

        var sawReplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawInjected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<SseEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var consumeTask = Task.Run(async () =>
        {
            await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: true, tail: null, replayFormat: "auto", cts.Token))
            {
                seen.Add(e);
                if (e.Name == "codex.notification" && e.Data.Contains("early", StringComparison.Ordinal))
                {
                    sawReplay.TrySetResult();
                }
                if (e.Name == "codex.notification" && e.Data.Contains("injected", StringComparison.Ordinal))
                {
                    sawInjected.TrySetResult();
                }
                if (e.Name == "run.completed")
                {
                    break;
                }
            }
        }, cts.Token);

        await Task.WhenAny(sawReplay.Task, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
        Assert.True(sawReplay.Task.IsCompleted);

        broadcaster.Publish(runId, injected);

        await Task.WhenAny(sawInjected.Task, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
        Assert.True(sawInjected.Task.IsCompleted);

        exec.AllowContinue.TrySetResult();
        await consumeTask;

        Assert.Contains(seen, e => e.Name == "codex.notification" && e.Data.Contains("injected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sse_ReplayFormat_Auto_PrefersRaw_WhenRawEventsArePersisted()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor(), persistRawEvents: true);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var store = host.App.Services.GetRequiredService<RunStore>();
        var dir = await store.TryResolveRunDirectoryAsync(runId, CancellationToken.None);
        Assert.NotNull(dir);
        Assert.True(File.Exists(Path.Combine(dir!, "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(dir!, "rollup.jsonl")));

        var events = new List<SseEvent>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: null, replayFormat: "auto", CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Contains(events, e => e.Name == "codex.notification");
        Assert.DoesNotContain(events, e => e.Name == "codex.rollup.outputLine");
        Assert.Contains(events, e => e.Name == "run.completed");
    }

    [Fact]
    public async Task Sse_ReplayFormat_Rollup_OverridesRaw_WhenRawEventsArePersisted()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor(), persistRawEvents: true);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var events = new List<SseEvent>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: null, replayFormat: "rollup", CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Contains(events, e => e.Name == "codex.rollup.outputLine");
        Assert.DoesNotContain(events, e => e.Name == "codex.notification");
        Assert.Contains(events, e => e.Name == "run.completed");
    }

    [Fact]
    public async Task Sse_ReplayFormat_Raw_StillEmitsRunCompleted_WhenRawMissing()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor(), persistRawEvents: false);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var events = new List<SseEvent>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: null, replayFormat: "raw", CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.DoesNotContain(events, e => e.Name == "codex.notification");
        Assert.Contains(events, e => e.Name == "run.completed");
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

    private static async Task WaitForEventAsync(RunnerClient client, Guid runId, string eventName, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await foreach (var e in client.GetEventsAsync(runId, replay: true, follow: true, tail: null, cts.Token))
            {
                if (e.Name == eventName)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Did not observe {eventName} for {runId:D} within {timeout}.");
        }

        throw new TimeoutException($"Did not observe {eventName} for {runId:D} within {timeout}.");
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
