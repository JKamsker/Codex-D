namespace CodexWebUi.Runner.CloudRunner.State;

public sealed record class Identity
{
    public required Guid RunnerId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

