using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace CodexWebUi.Runner.HttpRunner.Server;

public static class Auth
{
    public static bool IsAuthorized(HttpRequest request, string token)
    {
        if (!request.Headers.TryGetValue("Authorization", out var auth) || auth.Count == 0)
        {
            return false;
        }

        var value = auth.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = value["Bearer ".Length..].Trim();
        if (provided.Length == 0)
        {
            return false;
        }

        return FixedTimeEquals(provided, token);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
