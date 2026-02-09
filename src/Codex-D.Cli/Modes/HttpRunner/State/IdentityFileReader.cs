using System.Text.Json;

namespace CodexD.HttpRunner.State;

internal static class IdentityFileReader
{
    public static string? TryReadToken(string identityPath)
    {
        try
        {
            if (!File.Exists(identityPath))
            {
                return null;
            }

            var json = File.ReadAllText(identityPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("token", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var token = tokenEl.GetString();
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
        catch
        {
            return null;
        }
    }
}

