namespace CodexWebUi.Runner.CloudRunner.Contracts.Fs;

public sealed record class DirectoryEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
}


