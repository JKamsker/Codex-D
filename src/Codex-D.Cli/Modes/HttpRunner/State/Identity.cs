namespace CodexWebUi.Runner.HttpRunner.State;

public sealed record class Identity
{
    public required Guid RunnerId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Token { get; init; }
}

