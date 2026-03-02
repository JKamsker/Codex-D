using System.Globalization;
using System.Text.Json;
using CodexD.HttpRunner.Contracts.Threads;

namespace CodexD.HttpRunner.CodexRuntime;

internal static class ThreadApiParsing
{
    public static ThreadListResponse ParseThreadListResponse(JsonElement listResult)
    {
        var items = ParseThreadListThreads(listResult);
        var nextCursor = ExtractNextCursor(listResult);

        return new ThreadListResponse
        {
            Items = items,
            NextCursor = nextCursor,
            Raw = listResult.Clone()
        };
    }

    public static ThreadReadResponse ParseThreadReadResponse(JsonElement readResult)
    {
        var threadObject = TryGetObject(readResult, "thread") ?? readResult;
        var thread = ParseThreadSummary(threadObject);
        if (thread is null)
        {
            throw new InvalidOperationException("Failed to parse thread response.");
        }

        return new ThreadReadResponse
        {
            Thread = thread,
            Raw = readResult.Clone()
        };
    }

    public static IReadOnlyList<ThreadSummary> ParseThreadListThreads(JsonElement listResult)
    {
        var array =
            TryGetArray(listResult, "data") ??
            TryGetArray(listResult, "threads") ??
            TryGetArray(listResult, "items") ??
            TryGetArray(listResult, "sessions");

        if (array is null)
        {
            return Array.Empty<ThreadSummary>();
        }

        var threads = new List<ThreadSummary>();
        foreach (var item in array.Value.EnumerateArray())
        {
            var summary = ParseThreadSummary(item);
            if (summary is not null)
            {
                threads.Add(summary);
            }
        }

        return threads;
    }

    public static ThreadSummary? ParseThreadSummary(JsonElement threadObject)
    {
        if (threadObject.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var primary = TryGetObject(threadObject, "thread") ?? threadObject;

        var threadId = ExtractThreadId(threadObject);
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        var name =
            GetStringOrNull(primary, "name") ??
            GetStringOrNull(primary, "threadName") ??
            GetStringOrNull(primary, "title") ??
            GetStringOrNull(primary, "preview");

        var status = TryGetObject(primary, "status");
        var statusType = status is { } st ? GetStringOrNull(st, "type") : null;
        var activeFlags =
            string.Equals(statusType, "active", StringComparison.OrdinalIgnoreCase) &&
            status is { } sf
                ? GetOptionalStringArray(sf, "activeFlags")
                : null;

        var archived = GetBoolOrNull(primary, "archived");
        if (archived is null &&
            GetStringOrNull(primary, "path") is { Length: > 0 } path &&
            path.Contains("archived_sessions", StringComparison.OrdinalIgnoreCase))
        {
            archived = true;
        }

        var createdAt = GetDateTimeOffsetOrNull(primary, "createdAt");
        var cwd = GetStringOrNull(primary, "cwd");
        var model =
            GetStringOrNull(primary, "model") ??
            GetStringOrNull(primary, "modelProvider");

        return new ThreadSummary
        {
            ThreadId = threadId,
            Name = name,
            Archived = archived,
            StatusType = statusType,
            ActiveFlags = activeFlags,
            CreatedAt = createdAt,
            Cwd = cwd,
            Model = model,
            Raw = threadObject.Clone()
        };
    }

    public static string? ExtractNextCursor(JsonElement listResult) =>
        GetStringOrNull(listResult, "nextCursor") ??
        GetStringOrNull(listResult, "cursor");

    public static string? ExtractThreadId(JsonElement element)
    {
        return ExtractId(element, "threadId", "id") ??
               ExtractIdByPath(element, "thread", "threadId") ??
               ExtractIdByPath(element, "thread", "id") ??
               FindStringPropertyRecursive(element, "threadId", maxDepth: 6);
    }

    private static string? ExtractId(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }

        return null;
    }

    private static string? ExtractIdByPath(JsonElement element, string p1, string p2)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(p1, out var child) || child.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ExtractId(child, p2);
    }

    private static string? FindStringPropertyRecursive(JsonElement element, string propertyName, int maxDepth)
    {
        if (maxDepth < 0)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var value = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                foreach (var p in element.EnumerateObject())
                {
                    var found = FindStringPropertyRecursive(p.Value, propertyName, maxDepth - 1);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }

                return null;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    var found = FindStringPropertyRecursive(item, propertyName, maxDepth - 1);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }

                return null;
            }
            default:
                return null;
        }
    }

    private static JsonElement? TryGetArray(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.Array
            ? p
            : null;

    private static JsonElement? TryGetObject(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : null;

    private static string? GetStringOrNull(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static bool? GetBoolOrNull(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propertyName, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static IReadOnlyList<string>? GetOptionalStringArray(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propertyName, out var p))
        {
            return null;
        }

        if (p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (p.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString() ?? string.Empty);
            }
        }

        return list;
    }

    private static DateTimeOffset? GetDateTimeOffsetOrNull(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propertyName, out var p))
        {
            return null;
        }

        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            if (!string.IsNullOrWhiteSpace(s) &&
                DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                return dto;
            }
        }

        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var epoch))
        {
            return epoch > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }
}
