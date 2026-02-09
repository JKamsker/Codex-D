namespace CodexWebUi.Runner.CloudRunner.Configuration;

public sealed record class AppOptions
{
    public required Uri ServerUrl { get; init; }
    public required string ApiKey { get; init; }
    public required string Name { get; init; }
    public required string IdentityFile { get; init; }
    public required IReadOnlyList<string> WorkspaceRoots { get; init; }
    public required TimeSpan HeartbeatInterval { get; init; }
}

