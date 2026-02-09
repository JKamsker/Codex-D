using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexWebUi.Runner.HttpRunner.Contracts;

namespace CodexWebUi.Runner.HttpRunner.Client;

public sealed class RunnerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public RunnerClient(string baseUrl, string? token)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();

        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
    }

    public void Dispose() => _http.Dispose();

    public async Task<CreateRunResponse> CreateRunAsync(CreateRunRequest request, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(request, Json);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync($"{_baseUrl}/v1/runs", content, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {text}");
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var runId = root.GetProperty("runId").GetGuid();
        var status = root.GetProperty("status").GetString() ?? "unknown";
        return new CreateRunResponse { RunId = runId, Status = status };
    }

    public async Task<IReadOnlyList<Run>> ListRunsAsync(string? cwd, bool all, CancellationToken ct)
    {
        var url = all
            ? $"{_baseUrl}/v1/runs?all=true"
            : $"{_baseUrl}/v1/runs?cwd={Uri.EscapeDataString(cwd ?? string.Empty)}";

        using var res = await _http.GetAsync(url, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {text}");
        }

        using var doc = JsonDocument.Parse(text);
        var itemsEl = doc.RootElement.GetProperty("items");
        var items = JsonSerializer.Deserialize<List<Run>>(itemsEl.GetRawText(), Json) ?? new List<Run>();
        return items;
    }

    public async Task<Run> GetRunAsync(Guid runId, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"{_baseUrl}/v1/runs/{runId:D}", ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {text}");
        }

        return JsonSerializer.Deserialize<Run>(text, Json)
               ?? throw new InvalidOperationException("Invalid run JSON.");
    }

    public async Task InterruptAsync(Guid runId, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"{_baseUrl}/v1/runs/{runId:D}/interrupt", content: null, ct);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {text}");
        }
    }

    public async IAsyncEnumerable<SseEvent> GetEventsAsync(
        Guid runId,
        bool replay,
        bool follow,
        int? tail,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var query = new List<string>
        {
            $"replay={(replay ? "true" : "false")}",
            $"follow={(follow ? "true" : "false")}"
        };
        if (tail is { } n && n > 0)
        {
            query.Add($"tail={n}");
        }

        var url = $"{_baseUrl}/v1/runs/{runId:D}/events?{string.Join("&", query)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {text}");
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? eventName = null;
        var dataLines = new List<string>();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (eventName is not null)
                {
                    yield return new SseEvent { Name = eventName, Data = string.Join("\n", dataLines) };
                }

                eventName = null;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
                continue;
            }
        }
    }
}
