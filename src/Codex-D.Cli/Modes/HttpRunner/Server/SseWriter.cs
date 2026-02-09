using System.Text;
using Microsoft.AspNetCore.Http;

namespace CodexD.HttpRunner.Server;

public static class SseWriter
{
    public static void ConfigureHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-cache";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.ContentType = "text/event-stream";
    }

    public static async Task WriteEventAsync(
        HttpResponse response,
        string eventName,
        string dataJson,
        CancellationToken ct)
    {
        // SSE data lines must not contain raw newlines; if they do, split into multiple data lines.
        var sb = new StringBuilder();
        sb.Append("event: ").Append(eventName).Append('\n');

        using (var reader = new StringReader(dataJson))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                sb.Append("data: ").Append(line).Append('\n');
            }
        }

        sb.Append('\n');

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteCommentAsync(HttpResponse response, string comment, CancellationToken ct)
    {
        var payload = $": {comment}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }
}
