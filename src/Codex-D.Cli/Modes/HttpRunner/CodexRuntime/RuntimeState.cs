using JKToolKit.CodexSDK.AppServer.Resiliency;

namespace CodexD.HttpRunner.CodexRuntime;

public sealed class RuntimeState : IAppServerClientProvider
{
    private readonly object _lock = new();
    private ResilientCodexAppServerClient? _client;
    private TaskCompletionSource<ResilientCodexAppServerClient> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<ResilientCodexAppServerClient> GetClientAsync(CancellationToken ct = default)
    {
        Task<ResilientCodexAppServerClient> task;
        lock (_lock)
        {
            task = _client is not null ? Task.FromResult(_client) : _readyTcs.Task;
        }

        return new ValueTask<ResilientCodexAppServerClient>(task.WaitAsync(ct));
    }

    public ResilientCodexAppServerClient? TryGetClient()
    {
        lock (_lock)
        {
            return _client;
        }
    }

    public void SetClient(ResilientCodexAppServerClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        lock (_lock)
        {
            _client = client;
            _readyTcs.TrySetResult(client);
        }
    }

    public void ClearClient(ResilientCodexAppServerClient? client)
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
                _readyTcs = new TaskCompletionSource<ResilientCodexAppServerClient>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
