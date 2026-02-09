using System.Security.Cryptography;
using System.Text.Json;

namespace CodexWebUi.Runner.HttpRunner.State;

public sealed class IdentityStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _identityFile;

    public IdentityStore(string identityFile)
    {
        _identityFile = identityFile;
    }

    public async Task<Identity> LoadOrCreateAsync(string? tokenOverride, CancellationToken ct)
    {
        Identity? identity = null;
        try
        {
            if (File.Exists(_identityFile))
            {
                await using var stream = File.Open(_identityFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                identity = await JsonSerializer.DeserializeAsync<Identity>(stream, Json, ct);
            }
        }
        catch
        {
            identity = null;
        }

        if (!string.IsNullOrWhiteSpace(tokenOverride))
        {
            tokenOverride = tokenOverride.Trim();
            if (identity is null)
            {
                identity = new Identity { RunnerId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, Token = tokenOverride };
            }
            else
            {
                identity = identity with { Token = tokenOverride };
            }

            await WriteAsync(identity, ct);
            return identity;
        }

        if (identity is not null && identity.RunnerId != Guid.Empty && !string.IsNullOrWhiteSpace(identity.Token))
        {
            return identity;
        }

        identity = new Identity { RunnerId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, Token = GenerateToken() };
        await WriteAsync(identity, ct);
        return identity;
    }

    private async Task WriteAsync(Identity identity, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_identityFile);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(identity, Json);
        await File.WriteAllTextAsync(_identityFile, json, ct);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

