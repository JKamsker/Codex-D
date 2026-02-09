using System.Net.Http.Headers;
using System.Net;

namespace CodexD.HttpRunner.Client;

internal enum RunnerHealthStatus
{
    Ok,
    Unauthorized,
    Unreachable
}

internal static class RunnerHealth
{
    public static async Task<RunnerHealthStatus> CheckAsync(string baseUrl, string? token, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

        using var http = new HttpClient();
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        try
        {
            using var res = await http.GetAsync($"{baseUrl.TrimEnd('/')}/v1/health", timeoutCts.Token);
            if (res.IsSuccessStatusCode)
            {
                return RunnerHealthStatus.Ok;
            }

            return res.StatusCode == HttpStatusCode.Unauthorized
                ? RunnerHealthStatus.Unauthorized
                : RunnerHealthStatus.Unreachable;
        }
        catch
        {
            return RunnerHealthStatus.Unreachable;
        }
    }

    public static async Task<bool> IsHealthyAsync(string baseUrl, string? token, CancellationToken ct) =>
        await CheckAsync(baseUrl, token, ct) == RunnerHealthStatus.Ok;
}
