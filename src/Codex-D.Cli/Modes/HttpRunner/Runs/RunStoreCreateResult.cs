using CodexD.HttpRunner.Contracts;

namespace CodexD.HttpRunner.Runs;

public sealed record class RunStoreCreateResult
{
    public required Run Run { get; init; }
    public required string RunDirectory { get; init; }
}

