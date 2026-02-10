using System.Text.Json;
using CodexD.HttpRunner.CodexRuntime;
using CodexD.HttpRunner.Contracts;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.Notifications;
using JKToolKit.CodexSDK.Models;
using Microsoft.Extensions.Logging;

namespace CodexD.HttpRunner.Runs;

public sealed class CodexAppServerRunExecutor : IRunExecutor
{
    private readonly IAppServerClientProvider _codex;
    private readonly ILogger<CodexAppServerRunExecutor> _logger;

    public CodexAppServerRunExecutor(
        IAppServerClientProvider codex,
        ILogger<CodexAppServerRunExecutor> logger)
    {
        _codex = codex;
        _logger = logger;
    }

    public async Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        var client = await _codex.GetClientAsync(ct);

        var model = ResolveModelOrDefault(context.Model);
        var sandbox = ResolveSandboxOrDefault(context.Sandbox);
        var approvalPolicy = ResolveApprovalPolicyOrDefault(context.ApprovalPolicy);

        CodexThread thread;
        if (!string.IsNullOrWhiteSpace(context.CodexThreadId))
        {
            thread = await client.ResumeThreadAsync(
                new ThreadResumeOptions
                {
                    ThreadId = context.CodexThreadId,
                    Cwd = context.Cwd,
                    Model = model,
                    ApprovalPolicy = approvalPolicy,
                    Sandbox = sandbox
                },
                ct);
        }
        else
        {
            thread = await client.StartThreadAsync(
                new ThreadStartOptions
                {
                    Cwd = context.Cwd,
                    Model = model,
                    Sandbox = sandbox,
                    ApprovalPolicy = approvalPolicy,
                    Ephemeral = false
                },
                ct);
        }

        await context.SetCodexIdsAsync(thread.Id, null, ct);

        await using var turn = await client.StartTurnAsync(
            thread.Id,
            new TurnStartOptions
            {
                Input = [TurnInputItem.Text(context.Prompt)],
                Cwd = context.Cwd,
                Model = model,
                ApprovalPolicy = approvalPolicy,
                SandboxPolicy = null
            },
            ct);

        context.SetInterrupt(c => turn.InterruptAsync(c));
        await context.SetCodexIdsAsync(thread.Id, turn.TurnId, ct);

        await foreach (var notification in turn.Events(ct))
        {
            await context.PublishNotificationAsync(notification.Method, notification.Params, ct);
        }

        var completed = await turn.Completion;
        return MapCompletion(completed);
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

    private RunExecutionResult MapCompletion(TurnCompletedNotification completed)
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
}
