using System.Text.Json;

namespace CodexWebUi.Runner.CloudRunner.Contracts;

public sealed record class RunnerHeartbeatDto
{
    public required Guid RunnerId { get; init; }
    public JsonElement? Health { get; init; }
}


