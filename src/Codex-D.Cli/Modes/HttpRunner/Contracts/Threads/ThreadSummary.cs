using System.Text.Json;

namespace CodexD.HttpRunner.Contracts.Threads;

public sealed record class ThreadSummary
{
    public required string ThreadId { get; init; }
    public string? Name { get; init; }
    public bool? Archived { get; init; }
    public string? StatusType { get; init; }
    public IReadOnlyList<string>? ActiveFlags { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public required JsonElement Raw { get; init; }
}
