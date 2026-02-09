using System.Text.Json;

namespace CodexWebUi.Runner.CloudRunner.Commands;

public interface ICommandHandler
{
    Task<object?> HandleAsync(JsonElement payload, CancellationToken ct);
}


