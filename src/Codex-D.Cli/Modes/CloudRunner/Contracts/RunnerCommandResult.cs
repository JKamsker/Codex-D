using System.Text.Json;

namespace CodexWebUi.Runner.CloudRunner.Contracts;

public sealed record class RunnerCommandResult
{
    public required Guid CommandId { get; init; }
    public required bool Ok { get; init; }
    public JsonElement? Payload { get; init; }
    public RunnerCommandError? Error { get; init; }
}


