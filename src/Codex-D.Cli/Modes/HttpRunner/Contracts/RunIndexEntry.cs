namespace CodexD.HttpRunner.Contracts;

public sealed record class RunIndexEntry
{
    public required Guid RunId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Cwd { get; init; }
    public required string RelativeDir { get; init; }
}

