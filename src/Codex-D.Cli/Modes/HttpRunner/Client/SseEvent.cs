namespace CodexWebUi.Runner.HttpRunner.Client;

public sealed record class SseEvent
{
    public required string Name { get; init; }
    public required string Data { get; init; }
}

