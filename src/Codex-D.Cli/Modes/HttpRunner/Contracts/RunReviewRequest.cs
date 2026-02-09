namespace CodexD.HttpRunner.Contracts;

public sealed record class RunReviewRequest
{
    public bool Uncommitted { get; init; }
    public string? BaseBranch { get; init; }
    public string? CommitSha { get; init; }
    public string? Title { get; init; }
    public string[] AdditionalOptions { get; init; } = [];
}

