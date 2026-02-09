using JKToolKit.CodexSDK.AppServer;

namespace CodexWebUi.Runner.HttpRunner.CodexRuntime;

public sealed class RuntimeState : IAppServerClientProvider
{
    private readonly object _lock = new();
    private CodexAppServerClient? _client;
    private TaskCompletionSource<CodexAppServerClient> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<CodexAppServerClient> GetClientAsync(CancellationToken ct = default)
    {
        Task<CodexAppServerClient> task;
        lock (_lock)
        {
            task = _client is not null ? Task.FromResult(_client) : _readyTcs.Task;
        }

        return new ValueTask<CodexAppServerClient>(task.WaitAsync(ct));
    }

    public CodexAppServerClient? TryGetClient()
    {
        lock (_lock)
        {
            return _client;
        }
    }

    public void SetClient(CodexAppServerClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        lock (_lock)
        {
            _client = client;
            _readyTcs.TrySetResult(client);
        }
    }

    public void ClearClient(CodexAppServerClient? client)
    {
        lock (_lock)
        {
            if (_client is null)
            {
                return;
            }

            if (client is null || ReferenceEquals(_client, client))
            {
                _client = null;
                _readyTcs = new TaskCompletionSource<CodexAppServerClient>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
