using System.Text.Json;

namespace CodexD.HttpRunner.Daemon;

public static class DaemonRuntimeFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool TryRead(string path, out DaemonRuntimeInfo? info)
    {
        info = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            info = JsonSerializer.Deserialize<DaemonRuntimeInfo>(text, Json);
            return info is not null &&
                   !string.IsNullOrWhiteSpace(info.BaseUrl) &&
                   info.Port > 0 &&
                   !string.IsNullOrWhiteSpace(info.StateDir);
        }
        catch
        {
            info = null;
            return false;
        }
    }

    public static async Task WriteAtomicAsync(string path, DaemonRuntimeInfo info, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(info, Json);
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
    }
}

