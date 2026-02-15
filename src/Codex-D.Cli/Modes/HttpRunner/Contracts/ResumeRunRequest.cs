namespace CodexD.HttpRunner.Contracts;

public sealed record class ResumeRunRequest
{
    public string? Prompt { get; init; }
}
