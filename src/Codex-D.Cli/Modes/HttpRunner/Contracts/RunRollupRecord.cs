namespace CodexD.HttpRunner.Contracts;

public sealed record class RunRollupRecord
{
    // "outputLine" | "agentMessage"
    public required string Type { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    // outputLine fields
    public string? Source { get; init; } // e.g. "commandExecution"
    public string? Text { get; init; }
    public bool? EndsWithNewline { get; init; }
    public bool? IsControl { get; init; } // thinking/final markers
}

