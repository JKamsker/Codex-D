namespace CodexD.HttpRunner.Contracts;

public sealed record class CreateRunResponse
{
    public required Guid RunId { get; init; }
    public required string Status { get; init; }
}

