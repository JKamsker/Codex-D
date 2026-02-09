namespace CodexD.HttpRunner.Daemon;

public sealed record class DaemonRuntimeInfo
{
    public required string BaseUrl { get; init; }
    public required string Listen { get; init; }
    public required int Port { get; init; }
    public required int Pid { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required string StateDir { get; init; }
    public required string Version { get; init; }
}

