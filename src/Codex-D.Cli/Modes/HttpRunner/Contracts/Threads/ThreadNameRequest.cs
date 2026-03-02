namespace CodexD.HttpRunner.Contracts.Threads;

public sealed record class ThreadNameRequest
{
    public string? Name { get; init; }
}
