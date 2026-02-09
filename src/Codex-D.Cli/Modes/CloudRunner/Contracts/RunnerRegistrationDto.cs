namespace CodexWebUi.Runner.CloudRunner.Contracts;

public sealed record class RunnerRegistrationDto
{
    public required Guid RunnerId { get; init; }
    public required string Name { get; init; }
    public string? Hostname { get; init; }
    public string? Os { get; init; }
    public string? Arch { get; init; }
    public string? RunnerVersion { get; init; }
    public string? CodexVersion { get; init; }
    public IReadOnlyList<string>? WorkspaceRoots { get; init; }
}


