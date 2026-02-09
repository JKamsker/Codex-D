using System.Text.Json;

namespace CodexD.CloudRunner.Contracts;

public sealed record class RunnerCommandError
{
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }
    public JsonElement? Details { get; init; }
}


