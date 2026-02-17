namespace CodexD.HttpRunner.Contracts;

public sealed record class ThinkingSummaryItem
{
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Text { get; init; }
}

