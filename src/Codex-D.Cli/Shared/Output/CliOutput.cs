using System.Text.Json;

namespace CodexD.Shared.Output;

public static class CliOutput
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void WriteJsonLine(object value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, Json));

    public static void WriteJsonError(string code, string message, object? details = null)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = code, message, details }, Json));
    }
}
