using System.Text.Json;
using CodexD.HttpRunner.Contracts;
using CodexD.HttpRunner.State;

namespace CodexD.HttpRunner.Runs;

public sealed class RunStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _stateDirectory;
    private readonly string _runsRoot;
    private readonly string _indexFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RunStore(string stateDirectory)
    {
        _stateDirectory = stateDirectory;
        _runsRoot = StatePaths.RunsRoot(stateDirectory);
        _indexFile = StatePaths.RunsIndexFile(stateDirectory);
    }

    public string StateDirectory => _stateDirectory;

    public async Task<RunStoreCreateResult> CreateAsync(
        string cwd,
        string? kind,
        RunReviewRequest? review,
        string? model,
        string? sandbox,
        string? approvalPolicy,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();

        var runDirectory = GetRunDirectory(now, runId);
        Directory.CreateDirectory(runDirectory);

        var record = new Run
        {
            RunId = runId,
            CreatedAt = now,
            Cwd = cwd,
            Status = RunStatuses.Queued,
            StartedAt = null,
            CompletedAt = null,
            CodexThreadId = null,
            CodexTurnId = null,
            Kind = kind,
            Review = review,
            Model = model,
            Sandbox = sandbox,
            ApprovalPolicy = approvalPolicy,
            Error = null
        };

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_runsRoot);

            var relativeDir = Path.GetRelativePath(_runsRoot, runDirectory);
            var indexEntry = new RunIndexEntry { RunId = runId, CreatedAt = now, Cwd = cwd, RelativeDir = relativeDir };
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

    public async Task AppendEventAsync(Guid runId, RunEventEnvelope envelope, CancellationToken ct)
    {
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

    public async Task<IReadOnlyList<RunEventEnvelope>> ReadEventsAsync(
        Guid runId,
        int? tail,
        CancellationToken ct)
    {
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

        var queue = tail is { } n && n > 0 ? new Queue<RunEventEnvelope>(n) : null;
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
                if (queue.Count == tail)
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
            var dir = Path.Combine(_runsRoot, entry.RelativeDir);
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
                return Path.Combine(_runsRoot, entries[i].RelativeDir);
            }
        }

        return null;
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

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
