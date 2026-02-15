using System.Text.Json;
using CodexD.HttpRunner.Contracts;

namespace CodexD.HttpRunner.Runs;

public interface IRunExecutor
{
    Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct);
}

public sealed record class RunExecutionContext
{
    public required Guid RunId { get; init; }
    public required string Cwd { get; init; }
    public required string Prompt { get; init; }
    public string? CodexThreadId { get; init; }
    public string? Kind { get; init; }
    public RunReviewRequest? Review { get; init; }
    public string? Model { get; init; }
    public string? Sandbox { get; init; }
    public string? ApprovalPolicy { get; init; }
    public required Func<string, JsonElement, CancellationToken, Task> PublishNotificationAsync { get; init; }
    public required Func<string, string?, string?, CancellationToken, Task> SetCodexIdsAsync { get; init; }
    public required Action<Func<CancellationToken, Task>> SetInterrupt { get; init; }
}

public sealed record class RunExecutionResult
{
    public required string Status { get; init; }
    public string? Error { get; init; }
}
