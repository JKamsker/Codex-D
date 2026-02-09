using System.Text.Json;
using CodexWebUi.Runner.CloudRunner.Configuration;

namespace CodexWebUi.Runner.CloudRunner.State;

public sealed class IdentityStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AppOptions _options;

    public IdentityStore(AppOptions options)
    {
        _options = options;
    }

    public async Task<Identity> LoadOrCreateAsync(CancellationToken ct)
    {
        var file = _options.IdentityFile;

        try
        {
            if (File.Exists(file))
            {
                await using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var identity = await JsonSerializer.DeserializeAsync<Identity>(stream, Json, ct);
                if (identity is not null && identity.RunnerId != Guid.Empty)
                {
                    return identity;
                }
            }
        }
        catch
        {
            // fall through and overwrite
        }

        var created = new Identity { RunnerId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };

        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(created, Json), ct);

        return created;
    }
}

