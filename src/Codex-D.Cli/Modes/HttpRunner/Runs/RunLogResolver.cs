using System.Text.Json;
using CodexD.HttpRunner.Contracts;
using CodexD.HttpRunner.State;

namespace CodexD.HttpRunner.Runs;

internal static class RunLogResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<string> ResolveEventsFilePathAsync(
        string? filePath,
        string? stateDir,
        Guid? runId,
        bool isDev,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return Task.FromResult(Path.GetFullPath(filePath));
        }

        var explicitStateDir =
            TrimOrNull(stateDir) ??
            TrimOrNull(Environment.GetEnvironmentVariable("CODEX_D_DAEMON_STATE_DIR"));

        string resolvedStateDir;
        if (!string.IsNullOrWhiteSpace(explicitStateDir))
        {
            resolvedStateDir = Path.GetFullPath(explicitStateDir);
        }
        else
        {
            var candidates = isDev
                ? new[] { StatePaths.GetDaemonDevStateDir(), StatePaths.GetDaemonStateDir() }
                : new[] { StatePaths.GetDaemonStateDir(), StatePaths.GetDaemonDevStateDir() };

            resolvedStateDir = candidates
                .Select(Path.GetFullPath)
                .FirstOrDefault(d => File.Exists(StatePaths.RunsIndexFile(d)))
                ?? Path.GetFullPath(StatePaths.GetDefaultDaemonStateDir(isDev));
        }

        var runsRoot = StatePaths.RunsRoot(resolvedStateDir);
        var indexFile = StatePaths.RunsIndexFile(resolvedStateDir);

        if (!File.Exists(indexFile))
        {
            throw new FileNotFoundException($"Missing runs index file: {indexFile}");
        }

        var entry = runId is { } rid
            ? TryFindIndexEntry(indexFile, rid, ct)
            : TryFindLatestIndexEntry(indexFile, ct);

        if (entry is null)
        {
            var hint = runId is { } id ? id.ToString("D") : "<latest>";
            throw new InvalidOperationException($"Unable to resolve run directory for {hint}. Index file: {indexFile}");
        }

        var runDir = Path.Combine(runsRoot, entry.RelativeDir);
        return Task.FromResult(Path.Combine(runDir, "events.jsonl"));
    }

    private static RunIndexEntry? TryFindIndexEntry(string indexFile, Guid runId, CancellationToken ct)
    {
        RunIndexEntry? found = null;

        using var stream = File.Open(indexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RunIndexEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<RunIndexEntry>(line, Json);
            }
            catch
            {
                continue;
            }

            if (entry is null || entry.RunId == Guid.Empty)
            {
                continue;
            }

            if (entry.RunId == runId)
            {
                found = entry;
            }
        }

        return found;
    }

    private static RunIndexEntry? TryFindLatestIndexEntry(string indexFile, CancellationToken ct)
    {
        RunIndexEntry? latest = null;
        var latestCreated = DateTimeOffset.MinValue;

        using var stream = File.Open(indexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RunIndexEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<RunIndexEntry>(line, Json);
            }
            catch
            {
                continue;
            }

            if (entry is null || entry.RunId == Guid.Empty)
            {
                continue;
            }

            if (entry.CreatedAt > latestCreated)
            {
                latest = entry;
                latestCreated = entry.CreatedAt;
            }
        }

        return latest;
    }

    private static string? TrimOrNull(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
