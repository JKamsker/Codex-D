using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.Resiliency;
using CodexD.HttpRunner.Runs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexD.HttpRunner.CodexRuntime;

public sealed class ProcessHost : BackgroundService
{
    private readonly ICodexAppServerClientFactory _clientFactory;
    private readonly RuntimeState _state;
    private readonly RunManager _runs;
    private readonly IOptions<ProcessHostOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProcessHost> _logger;

    public ProcessHost(
        ICodexAppServerClientFactory clientFactory,
        RuntimeState state,
        RunManager runs,
        IOptions<ProcessHostOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<ProcessHost> logger)
    {
        _clientFactory = clientFactory;
        _state = state;
        _runs = runs;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var restartDelay = _options.Value.RestartDelay;
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            ResilientCodexAppServerClient? client = null;

            try
            {
                _logger.LogInformation("Starting codex app-server (resilient, attempt {Attempt})", attempt);

                var resilience = new CodexAppServerResilienceOptions
                {
                    AutoRestart = true,
                    NotificationsContinueAcrossRestarts = true,
                    EmitRestartMarkerNotifications = true,
                    RetryPolicy = CodexAppServerRetryPolicy.NeverRetry,
                    OnRestart = evt =>
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _runs.PauseAllInProgressAsync("codex runtime restarted", CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to mark in-progress runs as paused after codex restart.");
                            }
                        }, CancellationToken.None);
                    }
                };

                client = await ResilientCodexAppServerClient.StartAsync(
                    _clientFactory,
                    options: resilience,
                    loggerFactory: _loggerFactory,
                    ct: stoppingToken);

                _state.SetClient(client);

                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    if (client.State == CodexAppServerConnectionState.Faulted)
                    {
                        _logger.LogError("Resilient codex app-server client faulted; recreating.");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start or monitor resilient codex app-server client; retrying");
            }
            finally
            {
                _state.ClearClient(client);

                if (client is not null)
                {
                    try
                    {
                        await client.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing ResilientCodexAppServerClient.");
                    }
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(restartDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
