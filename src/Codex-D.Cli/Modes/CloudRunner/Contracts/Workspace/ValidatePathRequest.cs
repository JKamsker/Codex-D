namespace CodexD.CloudRunner.Contracts.Workspace;

public sealed record class ValidatePathRequest
{
    public required string Path { get; init; }
}


