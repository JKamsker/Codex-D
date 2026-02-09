using System.Text.Json;

namespace CodexD.HttpRunner.Contracts;

public sealed record class RunEventEnvelope
{
    public required string Type { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required JsonElement Data { get; init; }
}

