using System.Text.Json;
using CodexD.CloudRunner.Configuration;
using CodexD.CloudRunner.Contracts.Fs;
using CodexD.Shared.Paths;

namespace CodexD.CloudRunner.Commands;

public sealed class FsListDirectoriesHandler : ICommandHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AppOptions _options;

    public FsListDirectoriesHandler(AppOptions options)
    {
        _options = options;
    }

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var request = payload.Deserialize<ListDirectoriesRequest>(Json) ?? new ListDirectoriesRequest();

        string resolved;
        try
        {
            resolved = ResolveDirectoryPath(request.Path);
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CommandException("invalid_path", ex.Message);
        }

        List<DirectoryEntry> directories;
        try
        {
            directories = Directory
                .EnumerateDirectories(resolved)
                .Select(p => new DirectoryEntry { Name = Path.GetFileName(PathPolicy.TrimTrailingSeparators(p)), Path = p })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            throw new CommandException("list_failed", ex.Message);
        }

        var parent = TryGetParentPath(resolved);
        var roots = GetRoots(_options.WorkspaceRoots);

        return Task.FromResult<object?>(new DirectoryListingResponse
        {
            Path = resolved,
            ParentPath = parent,
            Directories = directories,
            Roots = roots
        });
    }

    private string ResolveDirectoryPath(string? input)
    {
        var trimmed = input?.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            if (_options.WorkspaceRoots.Count > 0)
            {
                foreach (var root in _options.WorkspaceRoots)
                {
                    try
                    {
                        var full = Path.GetFullPath(root);
                        if (Directory.Exists(full))
                        {
                            return full;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            return Directory.GetCurrentDirectory();
        }

        var combined = Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.Combine(Directory.GetCurrentDirectory(), trimmed);

        var fullPath = PathPolicy.TrimTrailingSeparators(Path.GetFullPath(combined));

        if (!Directory.Exists(fullPath))
        {
            throw new CommandException("not_found", "Directory does not exist.");
        }

        try
        {
            PathPolicy.EnsureWithinRoots(fullPath, _options.WorkspaceRoots);
        }
        catch (PathPolicyException ex)
        {
            throw new CommandException(ex.ErrorCode, ex.Message);
        }

        return fullPath;
    }

    private static string? TryGetParentPath(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            return di.Parent?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static List<DirectoryEntry> GetRoots(IReadOnlyList<string> workspaceRoots)
    {
        if (workspaceRoots.Count > 0)
        {
            return workspaceRoots
                .Select(r =>
                {
                    var full = Path.GetFullPath(r);
                    var name = Path.GetFileName(PathPolicy.TrimTrailingSeparators(full));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = full;
                    }
                    return new DirectoryEntry { Name = name, Path = full };
                })
                .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!OperatingSystem.IsWindows())
        {
            return new List<DirectoryEntry> { new() { Name = "/", Path = "/" } };
        }

        return DriveInfo
            .GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DirectoryEntry { Name = d.Name.TrimEnd('\\'), Path = d.RootDirectory.FullName })
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

