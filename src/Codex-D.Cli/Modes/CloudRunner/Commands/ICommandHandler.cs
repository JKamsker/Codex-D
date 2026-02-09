using System.Text.Json;

namespace CodexD.CloudRunner.Commands;

public interface ICommandHandler
{
    Task<object?> HandleAsync(JsonElement payload, CancellationToken ct);
}


