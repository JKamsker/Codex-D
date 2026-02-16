namespace CodexD.Shared.Strings;

public static class StringHelpers
{
    public static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
