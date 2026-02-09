namespace CodexD.CloudRunner.Contracts.Fs;

public sealed record class DirectoryListingResponse
{
    public required string Path { get; init; }
    public string? ParentPath { get; init; }
    public required List<DirectoryEntry> Directories { get; init; }
    public required List<DirectoryEntry> Roots { get; init; }
}


