using System.Text.Json;
using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Streaming;

/// <summary>
/// Fans <see cref="ChangeEvent"/>s out to connected <c>/api/events</c> clients. Publishing never blocks
/// (it runs inside store writes): each client has a bounded channel, and a client that falls
/// <see cref="ClientCapacity"/> events behind is disconnected. Its browser reconnects with
/// <c>Last-Event-ID</c> and gets <c>resync</c>. Event ids are <c>&lt;boot epoch&gt;-&lt;seq&gt;</c>;
/// there is no replay buffer. Singleton.
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
            var item = new SseItem<string>(change.Data, change.Type) { EventId = IdOf(_seq) };
            foreach (var subscriber in _subscribers)
            {
                if (subscriber.Filter is { } filter && filter != change.PipelineId) continue;
                if (subscriber.Writer.TryWrite(item)) continue;
                if (subscriber.Writer.TryComplete())
                    logger.LogInformation("Disconnecting a slow event stream client ({Capacity} events behind)", ClientCapacity);
            }
        }
    }

    /// <param name="lastEventId">The client's <c>Last-Event-ID</c>; when it is stale (or from another boot) the subscription needs a resync.</param>
    public ChangeSubscription Subscribe(Id<Pipeline>? filter, string? lastEventId)
    {
        var channel = Channel.CreateBounded<SseItem<string>>(new BoundedChannelOptions(ClientCapacity)
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

    /// <summary>The client's stream: <c>hello</c> (with the <c>retry:</c> hint), <c>resync</c> when needed, then events and heartbeats until cancelled.</summary>
    public async IAsyncEnumerable<SseItem<string>> StreamAsync(
        Id<Pipeline>? filter, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var subscription = Subscribe(filter, lastEventId);
        yield return new SseItem<string>(JsonSerializer.Serialize(new StreamHello(subscription.CurrentId), StreamingJsonContext.Default.StreamHello), "hello")
        {
            EventId = subscription.CurrentId,
            ReconnectionInterval = SseStreams.Retry,
        };
        if (subscription.NeedsResync)
            yield return new SseItem<string>(JsonSerializer.Serialize(new StreamResync("missed-events"), StreamingJsonContext.Default.StreamResync), "resync");

        await foreach (var item in SseStreams.WithHeartbeat(subscription.Reader, time, SseStreams.HeartbeatInterval, ct))
            yield return item;
    }

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

/// <summary>One connected client. Dispose to unsubscribe.</summary>
public sealed class ChangeSubscription : IDisposable
{
    private readonly ChangeBroadcaster _owner;
    private readonly Channel<SseItem<string>> _channel;

    internal ChangeSubscription(ChangeBroadcaster owner, Id<Pipeline>? filter, Channel<SseItem<string>> channel, string currentId, bool needsResync)
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
    public ChannelReader<SseItem<string>> Reader => _channel.Reader;
    internal ChannelWriter<SseItem<string>> Writer => _channel.Writer;

    public void Dispose()
    {
        _owner.Remove(this);
        _channel.Writer.TryComplete();
    }
}
