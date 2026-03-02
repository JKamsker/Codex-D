using System.Text.Json;

namespace CodexD.HttpRunner.Contracts.Threads;

public sealed record class ThreadReadResponse
{
    public required ThreadSummary Thread { get; init; }
    public required JsonElement Raw { get; init; }
}
