namespace CodexD.CloudRunner.Contracts.Fs;

public sealed record class ListDirectoriesRequest
{
    public string? Path { get; init; }
}


