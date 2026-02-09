using JKToolKit.CodexSDK.AppServer;
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
    private readonly ILogger<ProcessHost> _logger;

    public ProcessHost(
        ICodexAppServerClientFactory clientFactory,
        RuntimeState state,
        RunManager runs,
        IOptions<ProcessHostOptions> options,
        ILogger<ProcessHost> logger)
    {
        _clientFactory = clientFactory;
        _state = state;
        _runs = runs;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var restartDelay = _options.Value.RestartDelay;
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            CodexAppServerClient? client = null;
            var unexpectedExit = false;

            try
            {
                _logger.LogInformation("Starting codex app-server (attempt {Attempt})", attempt);

                client = await _clientFactory.StartAsync(stoppingToken);
                _state.SetClient(client);

                var exitTask = client.ExitTask; 
                var monitorTask = exitTask ;
                var winner = await Task.WhenAny(monitorTask);
                if (winner == monitorTask && !stoppingToken.IsCancellationRequested)
                {
                    unexpectedExit = true;
                    _logger.LogWarning("codex app-server exited unexpectedly; restarting");
                }

                if (winner == monitorTask)
                {
                    try
                    {
                        await monitorTask;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "codex subprocess monitor task faulted.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                unexpectedExit = true;
                _logger.LogError(ex, "ProcessHost loop failed; restarting");
            }
            finally
            {
                if (unexpectedExit && !stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await _runs.FailAllInProgressAsync("codex runtime restarted", stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to mark in-progress runs as failed after runtime exit.");
                    }
                }

                _state.ClearClient(client);

                if (client is not null)
                {
                    try
                    {
                        await client.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing CodexAppServerClient.");
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
