using System.Collections.Concurrent;
using System.Text.Json;
using CodexD.CloudRunner.Configuration;
using CodexD.CloudRunner.Contracts;

namespace CodexD.CloudRunner.Commands;

public sealed class CommandRouter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, ICommandHandler> _handlers = new(StringComparer.Ordinal);
    private int _activeCommands;

    public CommandRouter(AppOptions options)
    {
        Register("workspace.validatePath", new WorkspaceValidatePathHandler(options));
        Register("fs.listDirectories", new FsListDirectoriesHandler(options));
    }

    public int ActiveCommands => Volatile.Read(ref _activeCommands);

    public void Register(string type, ICommandHandler handler)
    {
        _handlers[type] = handler;
    }

    public async Task<RunnerCommandResult> HandleAsync(RunnerCommand command, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(command.Type, out var handler))
        {
            return new RunnerCommandResult
            {
                CommandId = command.CommandId,
                Ok = false,
                Payload = null,
                Error = new RunnerCommandError { ErrorCode = "unknown_command", Message = $"Unknown command type '{command.Type}'.", Details = null }
            };
        }

        Interlocked.Increment(ref _activeCommands);
        try
        {
            var resultObject = await handler.HandleAsync(command.Payload, ct);
            var payload = resultObject is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(resultObject, Json);

            return new RunnerCommandResult { CommandId = command.CommandId, Ok = true, Payload = payload, Error = null };
        }
        catch (CommandException ex)
        {
            var details = ex.Details is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(ex.Details, Json);
            return new RunnerCommandResult
            {
                CommandId = command.CommandId,
                Ok = false,
                Payload = null,
                Error = new RunnerCommandError { ErrorCode = ex.ErrorCode, Message = ex.Message, Details = details }
            };
        }
        catch (Exception ex)
        {
            return new RunnerCommandResult
            {
                CommandId = command.CommandId,
                Ok = false,
                Payload = null,
                Error = new RunnerCommandError { ErrorCode = "exception", Message = ex.Message, Details = null }
            };
        }
        finally
        {
            Interlocked.Decrement(ref _activeCommands);
        }
    }
}

