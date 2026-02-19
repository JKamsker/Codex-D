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
        Assert.True(json.TryGetProperty("informationalVersion", out var informational));
        Assert.True(informational.ValueKind is JsonValueKind.String or JsonValueKind.Null);
    }

    [Fact]
    public async Task Shutdown_ReturnsAccepted()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor());
        using var client = host.CreateHttpClient(includeToken: false);

        var res = await client.PostAsync("/v1/shutdown", content: null);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }

    [Fact]
    public async Task Shutdown_RejectsMissingToken_WhenAuthRequired()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: true, new ImmediateSuccessExecutor());
        using var client = host.CreateHttpClient(includeToken: false);

        var res = await client.PostAsync("/v1/shutdown", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
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
    public async Task Runs_List_IncludesCodexLastNotificationAt_ForActiveRun()
    {
        var exec = new CoordinatedExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        await exec.FirstPublished.Task;

        var list = await sdk.ListRunsAsync(cwd, all: false, CancellationToken.None);
        Assert.Single(list);
        Assert.NotNull(list[0].CodexLastNotificationAt);

        exec.AllowContinue.TrySetResult();
        await WaitForEventAsync(sdk, runId, "run.completed", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Runs_Get_IncludesCodexLastNotificationAt_AfterCompletion()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new ImmediateSuccessExecutor());
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;

        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var run = await sdk.GetRunAsync(runId, CancellationToken.None);
        Assert.NotNull(run.CodexLastNotificationAt);
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
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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

        await Task.WhenAny(attached.Task, Task.Delay(TimeSpan.FromSeconds(5), cts.Token));
        Assert.True(attached.Task.IsCompleted);

        exec.AllowContinue.TrySetResult();
        await consumeTask;

        var notificationPayloads = seen
            .Where(e => e.Name is "codex.notification" or "codex.rollup.outputLine")
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
    public async Task Stop_PausesReviewRun_AndEmitsRunPaused()
    {
        var exec = new CoordinatedExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest
        {
            Cwd = cwd,
            Prompt = "hi",
            Kind = RunKinds.Review,
            Review = new RunReviewRequest { Uncommitted = true, Mode = "exec" },
            Model = null,
            Sandbox = null,
            ApprovalPolicy = "never"
        }, CancellationToken.None);
        var runId = created.RunId;

        await exec.FirstPublished.Task;

        await sdk.StopAsync(runId, CancellationToken.None);

        await WaitForEventAsync(sdk, runId, "run.paused", TimeSpan.FromSeconds(5));

        var run = await sdk.GetRunAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatuses.Paused, run.Status);
        Assert.Equal(RunKinds.Review, RunKinds.Normalize(run.Kind));
    }

    [Fact]
    public async Task Stop_WorksBeforeExecutorRegistersInterrupt()
    {
        var exec = new NonInterruptibleBlockingExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest
        {
            Cwd = cwd,
            Prompt = "hi",
            Model = null,
            Sandbox = null,
            ApprovalPolicy = "never"
        }, CancellationToken.None);
        var runId = created.RunId;

        await exec.Started.Task;

        await sdk.StopAsync(runId, CancellationToken.None);
        await WaitForEventAsync(sdk, runId, "run.paused", TimeSpan.FromSeconds(5));

        var run = await sdk.GetRunAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatuses.Paused, run.Status);
    }

    [Fact]
    public async Task Stop_WorksWhenInterruptHandlerThrows()
    {
        var exec = new InterruptThrowsBlockingExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest
        {
            Cwd = cwd,
            Prompt = "hi",
            Kind = RunKinds.Review,
            Review = new RunReviewRequest { Uncommitted = true, Mode = "exec" },
            Model = null,
            Sandbox = null,
            ApprovalPolicy = "never"
        }, CancellationToken.None);
        var runId = created.RunId;

        await exec.Started.Task;

        await sdk.StopAsync(runId, CancellationToken.None);
        await WaitForEventAsync(sdk, runId, "run.paused", TimeSpan.FromSeconds(5));

        var run = await sdk.GetRunAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatuses.Paused, run.Status);
        Assert.Equal(RunKinds.Review, RunKinds.Normalize(run.Kind));
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
    public async Task Resume_ReviewRun_ContinuesAsExecTurn_WithContinuationPrefix()
    {
        var exec = new StopThenCaptureContinuationExecutor();
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, exec);
        using var sdk = host.CreateSdkClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest
        {
            Cwd = cwd,
            Prompt = "hi",
            Kind = RunKinds.Review,
            Review = new RunReviewRequest { Uncommitted = true, Mode = "exec" },
            Model = null,
            Sandbox = null,
            ApprovalPolicy = "never"
        }, CancellationToken.None);
        var runId = created.RunId;

        await exec.FirstStarted.Task;

        await sdk.StopAsync(runId, CancellationToken.None);
        await WaitForEventAsync(sdk, runId, "run.paused", TimeSpan.FromSeconds(5));

        const string resumePrompt = "please continue";
        await sdk.ResumeAsync(runId, prompt: resumePrompt, CancellationToken.None);
        await exec.SecondStarted.Task;

        Assert.Equal(RunKinds.Exec, exec.SecondKind);
        Assert.Contains("continuation of the review process", exec.SecondPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(resumePrompt, exec.SecondPrompt, StringComparison.Ordinal);
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
        Assert.False(File.Exists(Path.Combine(dir!, "rollup.jsonl")));
        Assert.False(File.Exists(Path.Combine(dir!, "events.jsonl"))); // default is in-memory only

        var outputLines = new List<JsonElement>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: null, replayFormat: "rollup", CancellationToken.None))
        {
            if (e.Name == "codex.rollup.outputLine")
            {
                using var doc = JsonDocument.Parse(e.Data);
                outputLines.Add(doc.RootElement.Clone());
            }
        }

        Assert.Contains(outputLines, r =>
            r.TryGetProperty("text", out var t) &&
            t.ValueKind == JsonValueKind.String &&
            t.GetString() == "partial" &&
            r.TryGetProperty("endsWithNewline", out var n) &&
            (n.ValueKind == JsonValueKind.True || n.ValueKind == JsonValueKind.False) &&
            n.GetBoolean() == false);
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

        var lines = await ReadRollupOutputLinesAsync(sdk, runId);

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

        var lines = await ReadRollupOutputLinesAsync(sdk, runId);

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

        var outputLines = new List<JsonElement>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: null, replayFormat: "rollup", CancellationToken.None))
        {
            if (e.Name == "codex.rollup.outputLine")
            {
                using var doc = JsonDocument.Parse(e.Data);
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("isControl", out var isCtrl) && isCtrl.ValueKind == JsonValueKind.True)
                {
                    continue;
                }
                outputLines.Add(root);
            }
        }

        Assert.Single(outputLines);
        Assert.Equal("hello", outputLines[0].GetProperty("text").GetString());
        Assert.True(outputLines[0].GetProperty("endsWithNewline").GetBoolean());
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

        var rollup = new List<JsonElement>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: null, replayFormat: "rollup", CancellationToken.None))
        {
            if (e.Name != "codex.rollup.outputLine")
            {
                continue;
            }

            using var doc = JsonDocument.Parse(e.Data);
            rollup.Add(doc.RootElement.Clone());
        }

        Assert.Contains(rollup, r => r.GetProperty("isControl").GetBoolean() && string.Equals(r.GetProperty("text").GetString(), "thinking", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rollup, r => r.GetProperty("isControl").GetBoolean() && string.Equals(r.GetProperty("text").GetString(), "final", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rollup, r => r.GetProperty("isControl").ValueKind != JsonValueKind.True && r.GetProperty("text").GetString() == "**Phase 1**");
        Assert.Contains(rollup, r => r.GetProperty("isControl").ValueKind != JsonValueKind.True && r.GetProperty("text").GetString() == "**Phase 2**");
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

        var events = new List<SseEvent>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: null, replayFormat: "rollup", CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.DoesNotContain(events, e => e.Name == "codex.rollup.outputLine");
        Assert.DoesNotContain(events, e => e.Name == "codex.rollup.agentMessage");
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
            await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: true, tail: null, replayFormat: "raw", cts.Token))
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
    public async Task Sse_ReplayFormat_Auto_PrefersRollup_WhenRolloutIsAvailable()
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
        Assert.False(File.Exists(Path.Combine(dir!, "rollup.jsonl")));

        var events = new List<SseEvent>();
        await foreach (var e in sdk.GetEventsAsync(runId, replay: true, follow: false, tail: null, replayFormat: "auto", CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Contains(events, e => e.Name == "codex.rollup.outputLine");
        Assert.DoesNotContain(events, e => e.Name == "codex.notification");
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

    [Fact]
    public async Task RunMessages_UsesBacklogWhenNoRawEventsOrRollout()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new MessagesAndThinkingNoRolloutExecutor(), persistRawEvents: false);
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
    public async Task ThinkingSummaries_UsesBacklogWhenNoRawEventsOrRollout()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new MessagesAndThinkingNoRolloutExecutor(), persistRawEvents: false);
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

    [Fact]
    public async Task RunMessages_CombinesRolloutAndPendingBacklog()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new PartialRolloutMaterializationExecutor(), persistRawEvents: false);
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
    public async Task RunMessages_DedupesOverlappingMaterializedBacklog()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new RolloutOnlyMessagesExecutor(), persistRawEvents: false);
        using var sdk = host.CreateSdkClient(includeToken: false);
        using var http = host.CreateHttpClient(includeToken: false);

        var cwd = Path.Combine(host.StateDir, "repo");
        Directory.CreateDirectory(cwd);

        var created = await sdk.CreateRunAsync(new CreateRunRequest { Cwd = cwd, Prompt = "hi", Model = null, Sandbox = null, ApprovalPolicy = "never" }, CancellationToken.None);
        var runId = created.RunId;
        await WaitForTerminalAsync(sdk, runId, TimeSpan.FromSeconds(2));

        var backlog = host.App.Services.GetRequiredService<RunNotificationBacklog>();

        var now = DateTimeOffset.UtcNow;
        backlog.Add(runId, new RunEventEnvelope
        {
            Type = "codex.notification",
            CreatedAt = now,
            Data = JsonSerializer.SerializeToElement(new
            {
                method = "item/completed",
                @params = new { item = new { type = "agentMessage", text = "two" } }
            })
        });

        backlog.Add(runId, new RunEventEnvelope
        {
            Type = "codex.notification",
            CreatedAt = now.AddMilliseconds(1),
            Data = JsonSerializer.SerializeToElement(new
            {
                method = "item/completed",
                @params = new { item = new { type = "agentMessage", text = "three" } }
            })
        });

        var json = await http.GetFromJsonAsync<JsonElement>($"/v1/runs/{runId:D}/messages?count=3");
        var items = json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("one", items[0].GetProperty("text").GetString());
        Assert.Equal("two", items[1].GetProperty("text").GetString());
        Assert.Equal("three", items[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ThinkingSummaries_CombinesRolloutAndPendingBacklog()
    {
        await using var host = await RunnerHttpTestHost.StartAsync(requireAuth: false, new PartialRolloutMaterializationExecutor(), persistRawEvents: false);
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

    private static async Task<List<string>> ReadRollupOutputLinesAsync(RunnerClient client, Guid runId)
    {
        var lines = new List<string>();
        await foreach (var e in client.GetEventsAsync(runId, replay: true, follow: false, tail: null, replayFormat: "rollup", CancellationToken.None))
        {
            if (e.Name != "codex.rollup.outputLine")
            {
                continue;
            }

            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;

            if (root.TryGetProperty("isControl", out var isCtrl) && isCtrl.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var text = t.GetString();
                if (text is not null)
                {
                    lines.Add(text);
                }
            }
        }

        return lines;
    }
}
