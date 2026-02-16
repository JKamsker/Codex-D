using System.Text;
using System.Text.Json;
using CodexD.HttpRunner.Contracts;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.Exec;
using JKToolKit.CodexSDK.Models;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace CodexD.HttpRunner.Runs;

public sealed class CodexReviewRunExecutor : IRunExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int FlushThresholdChars = 2048;

    private readonly ICodexAppServerClientFactory _appServer;
    private readonly ILogger<CodexReviewRunExecutor> _logger;

    public CodexReviewRunExecutor(
        ICodexAppServerClientFactory appServer,
        ILogger<CodexReviewRunExecutor> logger)
    {
        _appServer = appServer;
        _logger = logger;
    }

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        var review = context.Review ?? new RunReviewRequest { Uncommitted = true, Mode = "exec" };
        var mode = NormalizeMode(review.Mode);

        return mode switch
        {
            ReviewExecutionMode.AppServer => await ExecuteAppServerAsync(context, review, ct),
            _ => await ExecuteExecAsync(context, review, ct)
        };
    }

    private async Task<RunExecutionResult> ExecuteExecAsync(RunExecutionContext context, RunReviewRequest review, CancellationToken ct)
    {
        var interruptCts = new CancellationTokenSource();
        context.SetInterrupt(_ =>
        {
            interruptCts.Cancel();
            return Task.CompletedTask;
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, interruptCts.Token);

        await context.SetCodexIdsAsync("review", null, null, linked.Token);

        var options = new CodexReviewOptions(context.Cwd)
        {
            Uncommitted = review.Uncommitted,
            BaseBranch = string.IsNullOrWhiteSpace(review.BaseBranch) ? null : review.BaseBranch.Trim(),
            CommitSha = string.IsNullOrWhiteSpace(review.CommitSha) ? null : review.CommitSha.Trim(),
            Title = string.IsNullOrWhiteSpace(review.Title) ? null : review.Title.Trim(),
            AdditionalOptions = review.AdditionalOptions
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray()
        };

        var prompt = (context.Prompt ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            options.Prompt = prompt;
        }

        await using var stdoutWriter = new StreamingNotificationTextWriter(
            delta => context.PublishNotificationAsync(
                "item/agentMessage/delta",
                JsonSerializer.SerializeToElement(new { delta }, Json),
                linked.Token),
            linked.Token);

        await using var stderrWriter = new StreamingNotificationTextWriter(
            delta => context.PublishNotificationAsync(
                "item/commandExecution/outputDelta",
                JsonSerializer.SerializeToElement(new { delta }, Json),
                linked.Token),
            linked.Token);

        try
        {
            await context.PublishNotificationAsync(
                "item/agentMessage/delta",
                JsonSerializer.SerializeToElement(new { delta = "Starting review...\n" }, Json),
                linked.Token);

            await using var client = new JKToolKit.CodexSDK.Exec.CodexClient();
            var result = await client.ReviewAsync(options, stdoutWriter, stderrWriter, linked.Token);

            if (!string.IsNullOrWhiteSpace(result.LogPath))
            {
                await context.SetCodexIdsAsync("review", null, result.LogPath, linked.Token);
            }

            var status = result.ExitCode == 0 ? RunStatuses.Succeeded : RunStatuses.Failed;
            var error = result.ExitCode == 0 ? null : LimitError(result.StandardError);

            return new RunExecutionResult { Status = status, Error = error };
        }
        catch (OperationCanceledException) when (interruptCts.IsCancellationRequested)
        {
            return new RunExecutionResult { Status = RunStatuses.Interrupted, Error = null };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new RunExecutionResult { Status = RunStatuses.Interrupted, Error = null };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Review run failed.");
            return new RunExecutionResult { Status = RunStatuses.Failed, Error = LimitError(ex.Message) };
        }
    }

    private async Task<RunExecutionResult> ExecuteAppServerAsync(RunExecutionContext context, RunReviewRequest review, CancellationToken ct)
    {
        if (review.AdditionalOptions is { Length: > 0 })
        {
            return new RunExecutionResult
            {
                Status = RunStatuses.Failed,
                Error = "App-server review does not support AdditionalOptions. Use review.mode=exec or omit extra args."
            };
        }

        var delivery = ParseDelivery(review.Delivery);

        var model = ResolveModelOrDefault(context.Model);
        var sandbox = ResolveSandboxOrDefault(context.Sandbox);
        var approvalPolicy = ResolveApprovalPolicyOrDefault(context.ApprovalPolicy);

        await using var client = await _appServer.StartAsync(ct);

        var instructions = string.IsNullOrWhiteSpace(context.Prompt) ? null : context.Prompt.Trim();

        var thread = await client.StartThreadAsync(
            new ThreadStartOptions
            {
                Cwd = context.Cwd,
                Model = model,
                Sandbox = sandbox,
                ApprovalPolicy = approvalPolicy,
                Ephemeral = false,
                DeveloperInstructions = instructions
            },
            ct);

        var rolloutPath = CodexThreadRolloutPathExtractor.TryExtract(thread.Raw);
        await context.SetCodexIdsAsync(thread.Id, null, rolloutPath, ct);

        await context.PublishNotificationAsync(
            "item/agentMessage/delta",
            JsonSerializer.SerializeToElement(new { delta = "Starting review...\n" }, Json),
            ct);

        var target = BuildReviewTarget(review);

        await using var turn = (await client.StartReviewAsync(
            new ReviewStartOptions
            {
                ThreadId = thread.Id,
                Delivery = delivery,
                Target = target
            },
            ct)).Turn;

        context.SetInterrupt(c => turn.InterruptAsync(c));
        await context.SetCodexIdsAsync(turn.ThreadId, turn.TurnId, rolloutPath, ct);

        await foreach (var notification in turn.Events(ct))
        {
            await context.PublishNotificationAsync(notification.Method, notification.Params, ct);
        }

        var completed = await turn.Completion;
        return MapCompletion(completed);
    }

    private static string? LimitError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        error = error.Trim();
        const int max = 64 * 1024;
        return error.Length <= max ? error : error[..max];
    }

    private static ReviewDelivery? ParseDelivery(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.ToLowerInvariant() switch
        {
            "inline" => ReviewDelivery.Inline,
            "detached" => ReviewDelivery.Detached,
            _ => throw new ArgumentException("Invalid review delivery. Use 'inline' or 'detached'.", nameof(raw))
        };
    }

    private static ReviewTarget BuildReviewTarget(RunReviewRequest review)
    {
        if (review.Uncommitted)
        {
            return new ReviewTarget.UncommittedChanges();
        }

        if (!string.IsNullOrWhiteSpace(review.BaseBranch))
        {
            return new ReviewTarget.BaseBranch(review.BaseBranch.Trim());
        }

        if (!string.IsNullOrWhiteSpace(review.CommitSha))
        {
            var sha = review.CommitSha.Trim();
            var title = string.IsNullOrWhiteSpace(review.Title) ? null : review.Title.Trim();
            return new ReviewTarget.Commit(sha, title);
        }

        return new ReviewTarget.UncommittedChanges();
    }

    private static CodexModel ResolveModelOrDefault(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return CodexModel.Default;
        }

        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return CodexModel.Default;
        }

        return CodexModel.Parse(raw);
    }

    private static CodexSandboxMode ResolveSandboxOrDefault(string? raw)
    {
        raw = raw?.Trim();
        if (CodexSandboxMode.TryParse(raw, out var mode))
        {
            return mode;
        }

        return CodexSandboxMode.WorkspaceWrite;
    }

    private static CodexApprovalPolicy ResolveApprovalPolicyOrDefault(string? raw)
    {
        raw = raw?.Trim();
        if (CodexApprovalPolicy.TryParse(raw, out var policy))
        {
            return policy;
        }

        return CodexApprovalPolicy.Never;
    }

    private RunExecutionResult MapCompletion(JKToolKit.CodexSDK.AppServer.Notifications.TurnCompletedNotification completed)
    {
        var status = completed.Status?.Trim().ToLowerInvariant() switch
        {
            "completed" => RunStatuses.Succeeded,
            "failed" => RunStatuses.Failed,
            "interrupted" => RunStatuses.Interrupted,
            _ => RunStatuses.Succeeded
        };

        string? error = null;
        try
        {
            error = completed.Error?.GetRawText();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to serialize turn error payload.");
        }

        return new RunExecutionResult { Status = status, Error = error };
    }

    private enum ReviewExecutionMode
    {
        Exec,
        AppServer
    }

    private static ReviewExecutionMode NormalizeMode(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ReviewExecutionMode.Exec;
        }

        return raw.ToLowerInvariant() switch
        {
            "appserver" => ReviewExecutionMode.AppServer,
            "app-server" => ReviewExecutionMode.AppServer,
            _ => ReviewExecutionMode.Exec
        };
    }

    private sealed class StreamingNotificationTextWriter : TextWriter
    {
        private readonly Func<string, Task> _publish;
        private readonly CancellationToken _ct;
        private readonly Channel<string> _channel;
        private readonly Task _drainTask;
        private readonly StringBuilder _buffer = new();
        private readonly object _lock = new();

        public StreamingNotificationTextWriter(Func<string, Task> publish, CancellationToken ct)
        {
            _publish = publish;
            _ct = ct;
            _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = true,
                SingleReader = true,
                SingleWriter = false
            });
            _drainTask = Task.Run(DrainAsync, CancellationToken.None);
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _buffer.Append(value);
                if (value == '\n' || _buffer.Length >= FlushThresholdChars)
                {
                    EnqueueLocked();
                }
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_lock)
            {
                _buffer.Append(buffer, index, count);
                if (_buffer.Length >= FlushThresholdChars || Array.IndexOf(buffer, '\n', index, count) >= 0)
                {
                    EnqueueLocked();
                }
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (_lock)
            {
                _buffer.Append(value);
                if (_buffer.Length >= FlushThresholdChars || value.Contains('\n', StringComparison.Ordinal))
                {
                    EnqueueLocked();
                }
            }
        }

        public override async Task FlushAsync()
        {
            lock (_lock)
            {
                EnqueueLocked();
            }

            await Task.CompletedTask;
        }

        private void EnqueueLocked()
        {
            if (_buffer.Length == 0)
            {
                return;
            }

            var text = _buffer.ToString();
            _buffer.Clear();
            _channel.Writer.TryWrite(text);
        }

        private async Task DrainAsync()
        {
            try
            {
                await foreach (var chunk in _channel.Reader.ReadAllAsync(_ct))
                {
                    await _publish(chunk);
                }
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                // ignore cancellation
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await FlushAsync();
            }
            catch
            {
                // ignore
            }

            _channel.Writer.TryComplete();

            try
            {
                await _drainTask;
            }
            catch
            {
                // ignore
            }
        }
    }
}
