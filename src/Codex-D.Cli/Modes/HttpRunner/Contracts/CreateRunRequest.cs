namespace CodexD.HttpRunner.Contracts;

public sealed record class CreateRunRequest
{
    public required string Cwd { get; init; }
    public required string Prompt { get; init; }
    public string? Kind { get; init; }
    public RunReviewRequest? Review { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? Sandbox { get; init; }
    public string? ApprovalPolicy { get; init; }
}

