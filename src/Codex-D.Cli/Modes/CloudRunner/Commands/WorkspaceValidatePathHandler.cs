using System.Text.Json;
using CodexWebUi.Runner.CloudRunner.Configuration;
using CodexWebUi.Runner.CloudRunner.Contracts.Workspace;
using CodexWebUi.Runner.Shared.Paths;

namespace CodexWebUi.Runner.CloudRunner.Commands;

public sealed class WorkspaceValidatePathHandler : ICommandHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AppOptions _options;

    public WorkspaceValidatePathHandler(AppOptions options)
    {
        _options = options;
    }

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var request = payload.Deserialize<ValidatePathRequest>(Json);
        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            throw new CommandException("invalid_request", "Path is required.");
        }

        var trimmed = request.Path.Trim();

        string resolved;
        try
        {
            resolved = Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(Directory.GetCurrentDirectory(), trimmed);

            resolved = Path.GetFullPath(resolved);
        }
        catch (Exception ex)
        {
            throw new CommandException("invalid_path", $"Invalid path: {ex.Message}");
        }

        var normalized = PathPolicy.TrimTrailingSeparators(resolved);

        if (!Directory.Exists(normalized))
        {
            throw new CommandException("not_found", "Directory does not exist.");
        }

        try
        {
            PathPolicy.EnsureWithinRoots(normalized, _options.WorkspaceRoots);
        }
        catch (PathPolicyException ex)
        {
            throw new CommandException(ex.ErrorCode, ex.Message);
        }

        return Task.FromResult<object?>(new ValidatePathResponse { NormalizedPath = normalized });
    }
}

