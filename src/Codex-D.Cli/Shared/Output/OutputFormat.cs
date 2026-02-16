namespace CodexD.Shared.Output;

public enum OutputFormat
{
    Human = 0,
    Json = 1,
    Jsonl = 2
}

public enum OutputFormatUsage
{
    Single = 0,
    Streaming = 1
}

public static class OutputFormatParser
{
    public static OutputFormat Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var v = value.Trim();
        if (v.Length == 0)
        {
            throw new ArgumentException("Output format cannot be empty.", nameof(value));
        }

        v = v
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();

        return v switch
        {
            "human" => OutputFormat.Human,
            "text" => OutputFormat.Human,
            "pretty" => OutputFormat.Human,

            "json" => OutputFormat.Json,

            "jsonl" => OutputFormat.Jsonl,
            "ndjson" => OutputFormat.Jsonl,

            _ => throw new ArgumentException("Invalid --outputformat. Use 'human', 'json', or 'jsonl'.", nameof(value))
        };
    }

    public static OutputFormat Resolve(string? outputFormatOption, bool jsonFlag, OutputFormatUsage usage)
    {
        if (!string.IsNullOrWhiteSpace(outputFormatOption))
        {
            var parsed = Parse(outputFormatOption);
            return usage == OutputFormatUsage.Streaming && parsed == OutputFormat.Json ? OutputFormat.Jsonl : parsed;
        }

        if (jsonFlag)
        {
            return usage == OutputFormatUsage.Streaming ? OutputFormat.Jsonl : OutputFormat.Json;
        }

        return OutputFormat.Human;
    }
}
