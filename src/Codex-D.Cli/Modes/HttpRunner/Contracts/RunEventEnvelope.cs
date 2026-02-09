using System.Text.Json;

namespace CodexWebUi.Runner.HttpRunner.Contracts;

public sealed record class RunEventEnvelope
{
    public required string Type { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required JsonElement Data { get; init; }
}

