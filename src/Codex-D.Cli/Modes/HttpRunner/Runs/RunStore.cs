using System.Text.Json;
using CodexD.HttpRunner.Contracts;
using CodexD.HttpRunner.State;

namespace CodexD.HttpRunner.Runs;

public sealed class RunCreateOptions
{
    public required string Cwd { get; init; }
    public string? Kind { get; init; }
    public RunReviewRequest? Review { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? Sandbox { get; init; }
    public string? ApprovalPolicy { get; init; }
}

public sealed class RunStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxTailRecords = 200_000;

    private readonly string _stateDirectory;
    private readonly string _runsRoot;
    private readonly string _runsRootFull;
    private readonly string _runsRootPrefix;
    private readonly string _indexFile;
    private readonly bool _persistRawEvents;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RunStore(string stateDirectory, bool persistRawEvents = false)
    {
        _stateDirectory = stateDirectory;
        _runsRoot = StatePaths.RunsRoot(stateDirectory);
        _runsRootFull = Path.GetFullPath(_runsRoot);
        _runsRootPrefix = EnsureTrailingSeparator(_runsRootFull);
        _indexFile = StatePaths.RunsIndexFile(stateDirectory);
        _persistRawEvents = persistRawEvents;
    }

    public string StateDirectory => _stateDirectory;
    public bool PersistRawEvents => _persistRawEvents;

    [Obsolete("Use CreateAsync(RunCreateOptions, CancellationToken).")]
    public Task<RunStoreCreateResult> CreateAsync(
        string cwd,
        string? kind,
        RunReviewRequest? review,
        string? model,
        string? effort,
        string? sandbox,
        string? approvalPolicy,
        CancellationToken ct)
    {
        return CreateAsync(
            new RunCreateOptions
            {
                Cwd = cwd,
                Kind = kind,
                Review = review,
                Model = model,
                Effort = effort,
                Sandbox = sandbox,
                ApprovalPolicy = approvalPolicy
            },
            ct);
    }

    public async Task<RunStoreCreateResult> CreateAsync(RunCreateOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Cwd);

        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();

        var runDirectory = GetRunDirectory(now, runId);
        Directory.CreateDirectory(runDirectory);

        var record = new Run
        {
            RunId = runId,
            CreatedAt = now,
            Cwd = options.Cwd,
            Status = RunStatuses.Queued,
            StartedAt = null,
            CompletedAt = null,
            CodexThreadId = null,
            CodexTurnId = null,
            Kind = options.Kind,
            Review = options.Review,
            Model = options.Model,
            Effort = options.Effort,
            Sandbox = options.Sandbox,
            ApprovalPolicy = options.ApprovalPolicy,
            Error = null
        };

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_runsRoot);

            var relativeDir = Path.GetRelativePath(_runsRoot, runDirectory);
            var indexEntry = new RunIndexEntry { RunId = runId, CreatedAt = now, Cwd = options.Cwd, RelativeDir = relativeDir };
            await AppendLineAsync(_indexFile, JsonSerializer.Serialize(indexEntry, Json), ct);

            await WriteRunRecordAsync(runDirectory, record, ct);
        }
        finally
        {
            _gate.Release();
        }

        return new RunStoreCreateResult { Run = record, RunDirectory = runDirectory };
    }

    public async Task<Run?> TryGetAsync(Guid runId, CancellationToken ct)
    {
        var dir = await TryResolveRunDirectoryAsync(runId, ct);
        if (dir is null)
        {
            return null;
        }

        var file = Path.Combine(dir, "run.json");
        if (!File.Exists(file))
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            await using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await JsonSerializer.DeserializeAsync<Run>(stream, Json, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Guid runId, Run record, CancellationToken ct)
    {
        var dir = await TryResolveRunDirectoryAsync(runId, ct);
        if (dir is null)
        {
            throw new InvalidOperationException($"Unknown runId: {runId:D}");
        }

        await _gate.WaitAsync(ct);
        try
        {
            await WriteRunRecordAsync(dir, record, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendRawEventAsync(Guid runId, RunEventEnvelope envelope, CancellationToken ct)
    {
        if (!_persistRawEvents)
        {
            return;
        }

        var dir = await TryResolveRunDirectoryAsync(runId, ct);
        if (dir is null)
        {
            throw new InvalidOperationException($"Unknown runId: {runId:D}");
        }

        var file = Path.Combine(dir, "events.jsonl");
        var json = JsonSerializer.Serialize(envelope, Json);

        await _gate.WaitAsync(ct);
        try
        {
            await AppendLineAsync(file, json, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RunEventEnvelope>> ReadRawEventsAsync(
        Guid runId,
        int? tail,
        CancellationToken ct)
    {
        tail = ClampTail(tail);

        var dir = await TryResolveRunDirectoryAsync(runId, ct);
        if (dir is null)
        {
            throw new InvalidOperationException($"Unknown runId: {runId:D}");
        }

        var file = Path.Combine(dir, "events.jsonl");
        if (!File.Exists(file))
        {
            return Array.Empty<RunEventEnvelope>();
        }

        var tailN = tail is { } n && n > 0 ? n : 0;
        var queue = tailN > 0 ? new Queue<RunEventEnvelope>(tailN) : null;
        var list = queue is null ? new List<RunEventEnvelope>() : null;

        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RunEventEnvelope? env;
            try
            {
                env = JsonSerializer.Deserialize<RunEventEnvelope>(line, Json);
            }
            catch
            {
                continue;
            }

            if (env is null)
            {
                continue;
            }

            if (queue is not null)
            {
                if (queue.Count == tailN)
                {
                    queue.Dequeue();
                }
                queue.Enqueue(env);
            }
            else
            {
                list!.Add(env);
            }
        }

        return queue is not null ? queue.ToArray() : list!;
    }

    public async Task<IReadOnlyList<Run>> ListAsync(string? cwd, bool all, CancellationToken ct)
    {
        if (!all && string.IsNullOrWhiteSpace(cwd))
        {
            throw new ArgumentException("cwd is required unless all=true", nameof(cwd));
        }

        var entries = await ReadIndexAsync(ct);

        IEnumerable<RunIndexEntry> filtered = entries;
        if (!all)
        {
            filtered = filtered.Where(e => string.Equals(e.Cwd, cwd, GetPathComparison()));
        }

        var results = new List<Run>();
        foreach (var entry in filtered.OrderByDescending(e => e.CreatedAt))
        {
            var dir = TryGetSafeRunDirectory(entry.RelativeDir);
            if (dir is null)
            {
                continue;
            }
            var file = Path.Combine(dir, "run.json");
            if (!File.Exists(file))
            {
                continue;
            }

            await _gate.WaitAsync(ct);
            try
            {
                await using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var record = await JsonSerializer.DeserializeAsync<Run>(stream, Json, ct);
                if (record is not null)
                {
                    results.Add(record);
                }
            }
            catch
            {
                // ignore corrupt entries
            }
            finally
            {
                _gate.Release();
            }
        }

        return results;
    }

    public async Task<string?> TryResolveRunDirectoryAsync(Guid runId, CancellationToken ct)
    {
        var entries = await ReadIndexAsync(ct);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].RunId == runId)
            {
                var resolved = TryGetSafeRunDirectory(entries[i].RelativeDir);
                if (resolved is not null)
                {
                    return resolved;
                }
                break;
            }
        }

        // Index may be missing/corrupt (e.g. partial append). Try a best-effort scan of the runs folder
        // to keep the system resilient to index corruption.
        var scanned = TryScanForRunDirectory(runId, ct);
        if (scanned is null)
        {
            return null;
        }

        await TryRepairIndexAsync(runId, scanned, ct);
        return scanned;
    }

    public async IAsyncEnumerable<RunEventEnvelope> EnumerateRawEventsAsync(
        Guid runId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var dir = await TryResolveRunDirectoryAsync(runId, ct);
        if (dir is null)
        {
            throw new InvalidOperationException($"Unknown runId: {runId:D}");
        }

        var file = Path.Combine(dir, "events.jsonl");
        if (!File.Exists(file))
        {
            yield break;
        }

        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RunEventEnvelope? env;
            try
            {
                env = JsonSerializer.Deserialize<RunEventEnvelope>(line, Json);
            }
            catch
            {
                continue;
            }

            if (env is not null)
            {
                yield return env;
            }
        }
    }

    private async Task<IReadOnlyList<RunIndexEntry>> ReadIndexAsync(CancellationToken ct)
    {
        if (!File.Exists(_indexFile))
        {
            return Array.Empty<RunIndexEntry>();
        }

        var list = new List<RunIndexEntry>();

        using var stream = File.Open(_indexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
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

            if (entry is not null && entry.RunId != Guid.Empty)
            {
                list.Add(entry);
            }
        }

        return list;
    }

    private static async Task AppendLineAsync(string path, string line, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.AppendAllTextAsync(path, line + "\n", System.Text.Encoding.UTF8, ct);
    }

    private static async Task WriteRunRecordAsync(string runDirectory, Run record, CancellationToken ct)
    {
        var file = Path.Combine(runDirectory, "run.json");
        var json = JsonSerializer.Serialize(record, Json);
        var tmp = file + ".tmp";
        await File.WriteAllTextAsync(tmp, json, System.Text.Encoding.UTF8, ct);
        File.Move(tmp, file, overwrite: true);
    }

    private string GetRunDirectory(DateTimeOffset createdAtUtc, Guid runId)
    {
        var y = createdAtUtc.UtcDateTime.ToString("yyyy");
        var m = createdAtUtc.UtcDateTime.ToString("MM");
        var d = createdAtUtc.UtcDateTime.ToString("dd");
        return Path.Combine(_runsRoot, y, m, d, runId.ToString("D"));
    }

    private static int? ClampTail(int? tail)
    {
        if (tail is not { } n || n <= 0)
        {
            return null;
        }

        return Math.Min(n, MaxTailRecords);
    }

    private string? TryGetSafeRunDirectory(string relativeDir)
    {
        if (string.IsNullOrWhiteSpace(relativeDir))
        {
            return null;
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(_runsRootFull, relativeDir));
        }
        catch
        {
            return null;
        }

        if (!combined.StartsWith(_runsRootPrefix, GetPathComparison()))
        {
            return null;
        }

        return combined;
    }

    private string? TryScanForRunDirectory(Guid runId, CancellationToken ct)
    {
        if (!Directory.Exists(_runsRootFull))
        {
            return null;
        }

        var name = runId.ToString("D");
        var checkedDays = 0;
        const int maxDaysToScan = 5000;

        try
        {
            foreach (var yearDir in Directory.EnumerateDirectories(_runsRootFull))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (var monthDir in Directory.EnumerateDirectories(yearDir))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            foreach (var dayDir in Directory.EnumerateDirectories(monthDir))
                            {
                                ct.ThrowIfCancellationRequested();
                                checkedDays++;
                                if (checkedDays > maxDaysToScan)
                                {
                                    return null;
                                }

                                var candidate = Path.Combine(dayDir, name);
                                if (!Directory.Exists(candidate))
                                {
                                    continue;
                                }

                                var full = Path.GetFullPath(candidate);
                                if (full.StartsWith(_runsRootPrefix, GetPathComparison()))
                                {
                                    return full;
                                }
                            }
                        }
                        catch
                        {
                            // ignore unreadable month directory
                        }
                    }
                }
                catch
                {
                    // ignore unreadable year directory
                }
            }
        }
        catch
        {
            // ignore root enumeration issues
        }

        return null;
    }

    private async Task TryRepairIndexAsync(Guid runId, string runDirectory, CancellationToken ct)
    {
        var runFile = Path.Combine(runDirectory, "run.json");
        if (!File.Exists(runFile))
        {
            return;
        }

        Run? record;
        try
        {
            await using var stream = File.Open(runFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            record = await JsonSerializer.DeserializeAsync<Run>(stream, Json, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return;
        }

        if (record is null || record.RunId != runId)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var entries = await ReadIndexAsync(ct);
            if (entries.Any(e => e.RunId == runId))
            {
                return;
            }

            var relativeDir = Path.GetRelativePath(_runsRootFull, runDirectory);
            if (TryGetSafeRunDirectory(relativeDir) is null)
            {
                return;
            }

            var indexEntry = new RunIndexEntry
            {
                RunId = runId,
                CreatedAt = record.CreatedAt,
                Cwd = record.Cwd,
                RelativeDir = relativeDir
            };
            await AppendLineAsync(_indexFile, JsonSerializer.Serialize(indexEntry, Json), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
