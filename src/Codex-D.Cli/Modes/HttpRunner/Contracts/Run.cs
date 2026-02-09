namespace CodexD.HttpRunner.Contracts;

public sealed record class Run
{
    public required Guid RunId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Cwd { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? CodexThreadId { get; init; }
    public string? CodexTurnId { get; init; }
    public string? Kind { get; init; }
    public RunReviewRequest? Review { get; init; }
    public string? Model { get; init; }
    public string? Sandbox { get; init; }
    public string? ApprovalPolicy { get; init; }
    public string? Error { get; init; }
}

