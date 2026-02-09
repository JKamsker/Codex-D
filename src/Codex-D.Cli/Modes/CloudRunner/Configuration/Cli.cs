using System.Globalization;

namespace CodexD.CloudRunner.Configuration;

public static class Cli
{
    public static AppOptions Parse(string[] args)
    {
        var envServerUrl = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_SERVER_URL");
        var envApiKey = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_API_KEY");
        var envName = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_NAME");
        var envIdentityFile = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_IDENTITY_FILE");
        var envWorkspaceRoots = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_WORKSPACE_ROOTS");
        var envHeartbeatInterval = Environment.GetEnvironmentVariable("CODEXWEBUI_RUNNER_HEARTBEAT_INTERVAL");

        string? serverUrl = envServerUrl;
        string? apiKey = envApiKey;
        string? name = envName;
        string? identityFile = envIdentityFile;
        var workspaceRoots = ParseWorkspaceRoots(envWorkspaceRoots);
        var heartbeatInterval = ParseDurationOrDefault(envHeartbeatInterval, TimeSpan.FromSeconds(5));

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string NextValue()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ConfigurationException($"Missing value for '{arg}'.");
                }

                i++;
                return args[i];
            }

            switch (arg)
            {
                case "--server-url":
                    serverUrl = NextValue();
                    break;
                case "--api-key":
                    apiKey = NextValue();
                    break;
                case "--name":
                    name = NextValue();
                    break;
                case "--identity-file":
                    identityFile = NextValue();
                    break;
                case "--workspace-root":
                    workspaceRoots = workspaceRoots.Concat(new[] { NextValue() }).ToList();
                    break;
                case "--heartbeat-interval":
                    heartbeatInterval = ParseDurationOrThrow(NextValue(), "--heartbeat-interval");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new ConfigurationException("Missing server URL. Set CODEXWEBUI_RUNNER_SERVER_URL or pass --server-url.");
        }

        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var server))
        {
            throw new ConfigurationException($"Invalid server URL: '{serverUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ConfigurationException("Missing API key. Set CODEXWEBUI_RUNNER_API_KEY or pass --api-key.");
        }

        name ??= Environment.MachineName;
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.MachineName;
        }

        identityFile ??= GetDefaultIdentityFile();

        workspaceRoots = workspaceRoots
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AppOptions
        {
            ServerUrl = server,
            ApiKey = apiKey.Trim(),
            Name = name,
            IdentityFile = identityFile,
            WorkspaceRoots = workspaceRoots,
            HeartbeatInterval = heartbeatInterval
        };
    }

    private static List<string> ParseWorkspaceRoots(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TimeSpan ParseDurationOrDefault(string? raw, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return ParseDurationOrThrow(raw, "CODEXWEBUI_RUNNER_HEARTBEAT_INTERVAL");
    }

    private static TimeSpan ParseDurationOrThrow(string raw, string name)
    {
        raw = raw.Trim();

        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var ts))
        {
            return ts;
        }

        if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(raw[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        if (raw.EndsWith('s') &&
            double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            return TimeSpan.FromSeconds(s);
        }

        if (raw.EndsWith('m') &&
            double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
        {
            return TimeSpan.FromMinutes(m);
        }

        if (raw.EndsWith('h') &&
            double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
        {
            return TimeSpan.FromHours(h);
        }

        throw new ConfigurationException($"Invalid duration for '{name}': '{raw}'. Examples: '00:00:05', '5s', '250ms'.");
    }

    private static string GetDefaultIdentityFile()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = AppContext.BaseDirectory;
        }

        return Path.Combine(baseDir, "Codex-D", "identity.json");
    }
}

