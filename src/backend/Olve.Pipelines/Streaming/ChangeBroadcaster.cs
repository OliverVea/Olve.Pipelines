using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Olve.Pipelines.Pipelines;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Streaming;

/// <summary>
/// Fans change notices out to every subscribed stream (filtered per pipeline). Ids are <c>&lt;boot&gt;-&lt;seq&gt;</c>,
/// so a reconnecting client's <c>Last-Event-ID</c> says whether it missed anything; nothing is replayed.
/// </summary>
public sealed class ChangeBroadcaster(TimeProvider time, ILogger<ChangeBroadcaster> logger)
{
    public const int ClientCapacity = 256;

    private readonly Lock _lock = new();
    private readonly List<ChangeSubscription> _subscribers = [];
    private readonly Dictionary<Id<Pipeline>, long> _lastSeqByPipeline = [];
    private readonly string _epoch = time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    private long _seq;

    public int SubscriberCount { get { lock (_lock) return _subscribers.Count; } }

    public void Publish(ChangeEvent change)
    {
        lock (_lock)
        {
            _seq++;
            _lastSeqByPipeline[change.PipelineId] = _seq;
            var item = new ServerSentEvent<Contract.ChangeEvent>(change.Payload, IdOf(_seq));
            foreach (var subscriber in _subscribers)
            {
                if (subscriber.Filter is { } filter && filter != change.PipelineId) continue;
                if (subscriber.Writer.TryWrite(item)) continue;
                if (subscriber.Writer.TryComplete())
                    logger.LogInformation("Disconnecting a slow event stream client ({Capacity} events behind)", ClientCapacity);
            }
        }
    }

    public ChangeSubscription Subscribe(Id<Pipeline>? filter, string? lastEventId)
    {
        var channel = Channel.CreateBounded<ServerSentEvent<Contract.ChangeEvent>>(new BoundedChannelOptions(ClientCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        lock (_lock)
        {
            var latest = filter is { } id ? _lastSeqByPipeline.GetValueOrDefault(id) : _seq;
            var needsResync = lastEventId is not null && !(TryParse(lastEventId, out var seen) && seen >= latest);
            var subscription = new ChangeSubscription(this, filter, channel, IdOf(_seq), needsResync);
            _subscribers.Add(subscription);
            return subscription;
        }
    }

    public async IAsyncEnumerable<ServerSentEvent<Contract.ChangeEvent>> StreamAsync(
        Id<Pipeline>? filter, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var subscription = Subscribe(filter, lastEventId);
        yield return new ServerSentEvent<Contract.ChangeEvent>(new Contract.StreamHello { LastEventId = subscription.CurrentId }, subscription.CurrentId);
        if (subscription.NeedsResync)
            yield return new ServerSentEvent<Contract.ChangeEvent>(new Contract.StreamResync { Reason = "missed-events" });

        await foreach (var item in SseStreams.WithHeartbeat(subscription.Reader, time, SseStreams.HeartbeatInterval, Heartbeat, ct))
            yield return item;
    }

    public static ServerSentEvent<Contract.ChangeEvent> Heartbeat() => new(new Contract.Ping());

    internal void Remove(ChangeSubscription subscription)
    {
        lock (_lock) _subscribers.Remove(subscription);
    }

    private string IdOf(long seq) => $"{_epoch}-{seq.ToString(CultureInfo.InvariantCulture)}";

    private bool TryParse(string eventId, out long seq)
    {
        seq = 0;
        var dash = eventId.LastIndexOf('-');
        return dash > 0
               && eventId.AsSpan(0, dash).SequenceEqual(_epoch)
               && long.TryParse(eventId.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }
}

public sealed class ChangeSubscription : IDisposable
{
    private readonly ChangeBroadcaster _owner;
    private readonly Channel<ServerSentEvent<Contract.ChangeEvent>> _channel;

    internal ChangeSubscription(
        ChangeBroadcaster owner, Id<Pipeline>? filter, Channel<ServerSentEvent<Contract.ChangeEvent>> channel, string currentId, bool needsResync)
    {
        _owner = owner;
        _channel = channel;
        Filter = filter;
        CurrentId = currentId;
        NeedsResync = needsResync;
    }

    public Id<Pipeline>? Filter { get; }
    public string CurrentId { get; }
    public bool NeedsResync { get; }
    public ChannelReader<ServerSentEvent<Contract.ChangeEvent>> Reader => _channel.Reader;
    internal ChannelWriter<ServerSentEvent<Contract.ChangeEvent>> Writer => _channel.Writer;

    public void Dispose()
    {
        _owner.Remove(this);
        _channel.Writer.TryComplete();
    }
}
