using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace ClimaSense.Web.Sse;

/// <summary>
/// Singleton SSE broadcaster. Holds active subscribers and pushes
/// events to all of them as <c>event: &lt;type&gt;\ndata: &lt;json&gt;\nid: &lt;n&gt;\n\n</c>
/// frames. Slice 1 emits one event type only: <c>server-tick</c>.
/// </summary>
/// <remarks>
/// Slice 11 will register <c>breach-detected</c>; slice 13 will add
/// <c>clock-changed</c>. Subscribers are tracked per
/// <see cref="HttpContext.RequestAborted"/> and removed when the
/// browser closes the tab — verifying that closure is one of the
/// slice-1 acceptance criteria.
/// </remarks>
public sealed class AlertStream
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private long _nextEventId;

    public int SubscriberCount => _subscribers.Count;

    /// <summary>
    /// Register a new subscriber. The returned <see cref="IDisposable"/>
    /// must be disposed (typically via <c>using</c>) when the connection
    /// closes; that removes the subscriber from the registry.
    /// </summary>
    public Subscription Subscribe(long? lastEventId = null)
    {
        var subscriber = new Subscriber();
        if (!_subscribers.TryAdd(subscriber.Id, subscriber))
        {
            // GUIDs collide with effectively-zero probability; if the dict
            // already has the key something is badly wrong.
            throw new InvalidOperationException("Subscriber GUID collision.");
        }

        return new Subscription(this, subscriber, lastEventId);
    }

    /// <summary>
    /// Broadcast an event of the given type to every active subscriber.
    /// Returns the assigned event id. The payload is serialised camelCase.
    /// </summary>
    public long Broadcast(string eventType, object payload)
    {
        var eventId = Interlocked.Increment(ref _nextEventId);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var frame = new Frame(eventId, eventType, json);

        foreach (var (_, subscriber) in _subscribers)
        {
            // Drop frames for subscribers whose write-side is full rather
            // than blocking the broadcast loop. Slice 1 has only the
            // heartbeat firing every 15s so this branch should not trip.
            subscriber.Channel.Writer.TryWrite(frame);
        }

        return eventId;
    }

    internal void Remove(Guid subscriberId)
    {
        if (_subscribers.TryRemove(subscriberId, out var sub))
        {
            sub.Channel.Writer.TryComplete();
        }
    }

    internal sealed class Subscriber
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Channel<Frame> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<Frame>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
    }

    internal readonly record struct Frame(long EventId, string EventType, string DataJson);

    /// <summary>Active subscription handle. Dispose to unsubscribe.</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly AlertStream _owner;
        private readonly Subscriber _subscriber;
        private bool _disposed;

        internal Subscription(AlertStream owner, Subscriber subscriber, long? lastEventId)
        {
            _owner = owner;
            _subscriber = subscriber;
            LastEventId = lastEventId;
        }

        public Guid Id => _subscriber.Id;
        public long? LastEventId { get; }
        internal ChannelReader<Frame> Reader => _subscriber.Channel.Reader;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _owner.Remove(_subscriber.Id);
        }
    }
}
