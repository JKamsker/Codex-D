namespace CodexD.HttpRunner.Contracts.Threads;

public sealed record class ThreadForkRequest
{
    public bool PersistExtendedHistory { get; init; }
}
