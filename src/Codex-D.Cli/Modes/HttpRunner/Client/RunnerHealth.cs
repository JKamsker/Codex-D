using System.Net.Http.Headers;

namespace CodexD.HttpRunner.Client;

internal static class RunnerHealth
{
    public static async Task<bool> IsHealthyAsync(string baseUrl, string? token, CancellationToken ct)
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
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

