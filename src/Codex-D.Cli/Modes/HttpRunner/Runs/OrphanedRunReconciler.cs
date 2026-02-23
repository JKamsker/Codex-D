using CodexD.HttpRunner.Contracts;
using CodexD.HttpRunner.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexD.HttpRunner.Runs;

/// <summary>
/// On daemon/server restart, any previously "running" runs are no longer executing (RunManager is in-memory).
/// Reconcile such runs to a terminal/resumable status so the CLI doesn't show "running" forever.
/// </summary>
public sealed class OrphanedRunReconciler : IHostedService
{
    private static readonly TimeSpan StartedAtTolerance = TimeSpan.FromSeconds(5);

    private readonly ServerConfig _config;
    private readonly RunStore _store;
    private readonly RunManager _runs;
    private readonly ILogger<OrphanedRunReconciler> _logger;

    public OrphanedRunReconciler(
        ServerConfig config,
        RunStore store,
        RunManager runs,
        ILogger<OrphanedRunReconciler> logger)
    {
        _config = config;
        _store = store;
        _runs = runs;
        _logger = logger;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Run> all;
        try
        {
            all = await _store.ListAsync(cwd: null, all: true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list runs for orphan reconciliation.");
            return;
        }

        var reconciled = 0;
        foreach (var run in all)
        {
            if (!string.Equals(run.Status, RunStatuses.Running, StringComparison.Ordinal))
            {
                continue;
            }

            if (_runs.IsActive(run.RunId))
            {
                continue;
            }

            // If the run started before this server instance started, it is orphaned/stale.
            // Use a tolerance because clocks and serialization are not perfect.
            var cutoff = _config.StartedAtUtc - StartedAtTolerance;
            var started = run.StartedAt ?? run.CreatedAt;
            if (started >= cutoff)
            {
                continue;
            }

            var paused = run with
            {
                Status = RunStatuses.Paused,
                CompletedAt = null,
                Error = "orphaned after runner restart (was running during previous server instance)"
            };

            try
            {
                await _store.UpdateAsync(run.RunId, paused, cancellationToken);
                reconciled++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile orphaned run. runId={RunId}", run.RunId);
            }
        }

        if (reconciled > 0)
        {
            _logger.LogInformation(
                "Reconciled {Count} orphaned running runs to paused. serverStartedAtUtc={StartedAtUtc}",
                reconciled,
                _config.StartedAtUtc);
        }
    }
}

