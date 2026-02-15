namespace CodexD.HttpRunner.Contracts;

public sealed record class SteerRunRequest
{
    public required string Prompt { get; init; }
}

