namespace CodexWebUi.Runner.HttpRunner.Client;

public sealed record class ResolvedClientSettings
{
    public required string BaseUrl { get; init; }
    public string? Token { get; init; }
    public required string Cwd { get; init; }
}

