using System.Text;
using System.Text.Json;
using CodexD.HttpRunner.Contracts;
using JKToolKit.CodexSDK.Exec;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace CodexD.HttpRunner.Runs;

public sealed class CodexReviewRunExecutor : IRunExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int FlushThresholdChars = 2048;

    private readonly ILogger<CodexReviewRunExecutor> _logger;

    public CodexReviewRunExecutor(ILogger<CodexReviewRunExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        var review = context.Review ?? new RunReviewRequest { Uncommitted = true };

        var interruptCts = new CancellationTokenSource();
        context.SetInterrupt(_ =>
        {
            interruptCts.Cancel();
            return Task.CompletedTask;
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, interruptCts.Token);

        await context.SetCodexIdsAsync("review", null, linked.Token);

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
