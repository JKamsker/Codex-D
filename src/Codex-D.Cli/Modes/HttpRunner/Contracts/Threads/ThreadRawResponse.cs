using System.Text.Json;

namespace CodexD.HttpRunner.Contracts.Threads;

public sealed record class ThreadRawResponse
{
    public string? ThreadId { get; init; }
    public required JsonElement Raw { get; init; }
}
