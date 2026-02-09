using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CodexD.HttpRunner.Client;
using CodexD.HttpRunner.Runs;
using CodexD.HttpRunner.Server;
using CodexD.HttpRunner.State;
using Microsoft.Extensions.DependencyInjection;

namespace CodexD.Tests;

internal sealed class RunnerHttpTestHost : IAsyncDisposable
{
    private RunnerHttpTestHost(
        string baseUrl,
        int port,
        string stateDir,
        Identity identity,
        Microsoft.AspNetCore.Builder.WebApplication app)
    {
        BaseUrl = baseUrl;
        Port = port;
        StateDir = stateDir;
        Identity = identity;
        App = app;
    }

    public string BaseUrl { get; }
    public int Port { get; }
    public string StateDir { get; }
    public Identity Identity { get; }
    public Microsoft.AspNetCore.Builder.WebApplication App { get; }

    public static async Task<RunnerHttpTestHost> StartAsync(
        bool requireAuth,
        IRunExecutor executor,
        CancellationToken ct = default)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "codex-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);

        var port = GetFreeTcpPort();
        var token = "test-token";
        var identity = new Identity { RunnerId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, Token = token };

        var config = new ServerConfig
        {
            ListenAddress = IPAddress.Loopback,
            Port = port,
            RequireAuth = requireAuth,
            BaseUrl = $"http://127.0.0.1:{port}",
            StateDirectory = stateDir,
            Identity = identity,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var app = Host.Build(
            config,
            configureServices: services =>
            {
                services.AddSingleton<IRunExecutor>(executor);
            },
            enableCodexRuntime: false);

        await app.StartAsync(ct);
        return new RunnerHttpTestHost(config.BaseUrl, port, stateDir, identity, app);
    }

    public HttpClient CreateHttpClient(bool includeToken)
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        if (includeToken)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Identity.Token);
        }
        return client;
    }

    public RunnerClient CreateSdkClient(bool includeToken) =>
        new RunnerClient(BaseUrl, includeToken ? Identity.Token : null);

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await App.StopAsync(cts.Token);
        }
        catch
        {
            // ignore
        }

        try
        {
            await App.DisposeAsync();
        }
        catch
        {
            // ignore
        }

        try
        {
            Directory.Delete(StateDir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
