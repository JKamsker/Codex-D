namespace CodexD.HttpRunner.Contracts;

public sealed record class RunReviewRequest
{
    /// <summary>
    /// Review execution mode. Supported values:
    /// - "exec" (default): runs <c>codex review</c>
    /// - "appserver": runs app-server <c>review/start</c>
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// App-server review delivery mode ("inline" or "detached"). Only used when <see cref="Mode"/> is "appserver".
    /// </summary>
    public string? Delivery { get; init; }

    public bool Uncommitted { get; init; }
    public string? BaseBranch { get; init; }
    public string? CommitSha { get; init; }
    public string? Title { get; init; }
    public string[] AdditionalOptions { get; init; } = [];
}
