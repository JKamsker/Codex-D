using System.Collections.Concurrent;
using System.Threading.Channels;
using CodexD.HttpRunner.Contracts;

namespace CodexD.HttpRunner.Runs;

public sealed class RunEventBroadcaster
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<long, Channel<RunEventEnvelope>>> _subs = new();
    private long _nextId;

    public RunEventSubscription Subscribe(Guid runId)
    {
        var id = Interlocked.Increment(ref _nextId);
        var channel = Channel.CreateUnbounded<RunEventEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var dict = _subs.GetOrAdd(runId, _ => new ConcurrentDictionary<long, Channel<RunEventEnvelope>>());
        dict[id] = channel;

        return new RunEventSubscription(runId, channel.Reader, () => Unsubscribe(runId, id));
    }

    public void Publish(Guid runId, RunEventEnvelope envelope)
    {
        if (!_subs.TryGetValue(runId, out var dict))
        {
            return;
        }

        foreach (var kvp in dict)
        {
            kvp.Value.Writer.TryWrite(envelope);
        }
    }

    private void Unsubscribe(Guid runId, long subscriptionId)
    {
        if (!_subs.TryGetValue(runId, out var dict))
        {
            return;
        }

        if (dict.TryRemove(subscriptionId, out var channel))
        {
            channel.Writer.TryComplete();
        }

        if (dict.IsEmpty)
        {
            _subs.TryRemove(runId, out _);
        }
    }
}

public sealed class RunEventSubscription : IAsyncDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    public Guid RunId { get; }
    public ChannelReader<RunEventEnvelope> Reader { get; }

    public RunEventSubscription(Guid runId, ChannelReader<RunEventEnvelope> reader, Action dispose)
    {
        RunId = runId;
        Reader = reader;
        _dispose = dispose;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _dispose();
        return ValueTask.CompletedTask;
    }
}
