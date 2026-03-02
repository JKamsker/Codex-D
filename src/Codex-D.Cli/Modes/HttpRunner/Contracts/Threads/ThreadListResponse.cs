using System.Text.Json;

namespace CodexD.HttpRunner.Contracts.Threads;

public sealed record class ThreadListResponse
{
    public required IReadOnlyList<ThreadSummary> Items { get; init; }
    public string? NextCursor { get; init; }
    public required JsonElement Raw { get; init; }
}
