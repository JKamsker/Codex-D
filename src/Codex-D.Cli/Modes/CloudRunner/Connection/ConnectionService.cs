using System.Runtime.InteropServices;
using System.Text.Json;
using CodexD.CloudRunner.Commands;
using CodexD.CloudRunner.Configuration;
using CodexD.CloudRunner.Contracts;
using CodexD.CloudRunner.State;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexD.CloudRunner.Connection;

public sealed class ConnectionService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AppOptions _options;
    private readonly IdentityStore _identityStore;
    private readonly CommandRouter _router;
    private readonly ILogger<ConnectionService> _logger;

    private HubConnection? _connection;
    private Identity? _identity;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public ConnectionService(
        AppOptions options,
        IdentityStore identityStore,
        CommandRouter router,
        ILogger<ConnectionService> logger)
    {
        _options = options;
        _identityStore = identityStore;
        _router = router;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _identity = await _identityStore.LoadOrCreateAsync(stoppingToken);

        _connection = new HubConnectionBuilder()
            .WithUrl(BuildHubUrl(_options.ServerUrl), o =>
            {
                o.AccessTokenProvider = () => Task.FromResult(_options.ApiKey)!;
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        _connection.Closed += ex =>
        {
            if (ex is not null)
            {
                _logger.LogWarning(ex, "Runner SignalR connection closed.");
            }
            else
            {
                _logger.LogInformation("Runner SignalR connection closed.");
            }
            return Task.CompletedTask;
        };

        _connection.On<RunnerCommand>("Command", async command =>
        {
            if (_connection is null)
            {
                return;
            }

            var result = await _router.HandleAsync(command, stoppingToken);
            try
            {
                await _connection.InvokeAsync("CommandResult", result, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send CommandResult. commandId={CommandId}", command.CommandId);
            }
        });

        _connection.Reconnected += async _ =>
        {
            if (_identity is null)
            {
                return;
            }

            await RegisterAsync(_identity.RunnerId, stoppingToken);
        };

        _logger.LogInformation("Connecting to {ServerUrl} as {RunnerId}", _options.ServerUrl, _identity.RunnerId);

        await _connection.StartAsync(stoppingToken);
        await RegisterAsync(_identity.RunnerId, stoppingToken);

        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await HeartbeatAsync(_identity.RunnerId, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            await _connection.StopAsync(cancellationToken);
            await _connection.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task RegisterAsync(Guid runnerId, CancellationToken ct)
    {
        if (_connection is null)
        {
            return;
        }

        var registration = new RunnerRegistrationDto
        {
            RunnerId = runnerId,
            Name = _options.Name,
            Hostname = Environment.MachineName,
            Os = RuntimeInformation.OSDescription,
            Arch = RuntimeInformation.OSArchitecture.ToString(),
            RunnerVersion = typeof(ConnectionService).Assembly.GetName().Version?.ToString(),
            CodexVersion = null,
            WorkspaceRoots = _options.WorkspaceRoots.Count > 0 ? _options.WorkspaceRoots : null
        };

        await _connection.InvokeAsync("Register", registration, ct);
    }

    private async Task HeartbeatAsync(Guid runnerId, CancellationToken ct)
    {
        if (_connection is null)
        {
            return;
        }

        var health = JsonSerializer.SerializeToElement(new
        {
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            cpuCount = Environment.ProcessorCount,
            workingSetBytes = Environment.WorkingSet,
            activeCommands = _router.ActiveCommands
        }, Json);

        var heartbeat = new RunnerHeartbeatDto { RunnerId = runnerId, Health = health };
        await _connection.InvokeAsync("Heartbeat", heartbeat, ct);
    }

    private static Uri BuildHubUrl(Uri serverUrl)
    {
        var baseUri = serverUrl.ToString().TrimEnd('/');
        return new Uri($"{baseUri}/_hubs/runner");
    }
}

