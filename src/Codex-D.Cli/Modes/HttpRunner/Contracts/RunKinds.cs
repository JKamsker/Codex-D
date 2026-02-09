namespace CodexD.HttpRunner.Contracts;

public static class RunKinds
{
    public const string Exec = "exec";
    public const string Review = "review";

    public static string Normalize(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            null or "" => Exec,
            Exec => Exec,
            Review => Review,
            _ => raw!.Trim().ToLowerInvariant()
        };
}

