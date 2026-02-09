using System.Net;
using CodexD.HttpRunner.State;

namespace CodexD.HttpRunner.Server;

public sealed record class ServerConfig
{
    public required IPAddress ListenAddress { get; init; }
    public required int Port { get; init; }
    public required bool RequireAuth { get; init; }
    public required string BaseUrl { get; init; }
    public required string StateDirectory { get; init; }
    public required Identity Identity { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
}
