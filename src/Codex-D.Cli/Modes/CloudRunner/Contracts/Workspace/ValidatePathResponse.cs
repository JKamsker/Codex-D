namespace CodexD.CloudRunner.Contracts.Workspace;

public sealed record class ValidatePathResponse
{
    public required string NormalizedPath { get; init; }
}


