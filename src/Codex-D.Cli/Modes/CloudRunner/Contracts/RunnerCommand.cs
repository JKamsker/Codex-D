using System.Text.Json;

namespace CodexD.CloudRunner.Contracts;

public sealed record class RunnerCommand
{
    public required Guid CommandId { get; init; }
    public required string Type { get; init; }
    public required JsonElement Payload { get; init; }
}


