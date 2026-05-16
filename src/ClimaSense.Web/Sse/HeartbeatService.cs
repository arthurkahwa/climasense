using ClimaSense.Web.Clock;

namespace ClimaSense.Web.Sse;

/// <summary>
/// Background service that broadcasts a <c>server-tick</c> SSE event
/// every 15 seconds. Slice 1's only event source.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(15);

    private readonly AlertStream _stream;
    private readonly IClock _clock;
    private readonly ILogger<HeartbeatService> _logger;
    private long _tickCount;

    public HeartbeatService(AlertStream stream, IClock clock, ILogger<HeartbeatService> logger)
    {
        _stream = stream;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Emit one immediate tick so a freshly-connected client sees a
        // heartbeat in the first second instead of waiting 15 s.
        Emit();

        using var timer = new PeriodicTimer(Cadence);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Emit();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    private void Emit()
    {
        var tick = Interlocked.Increment(ref _tickCount);
        var ts = _clock.UtcNow();
        var payload = new
        {
            tick,
            ts = ts.ToString("O"),
            subscribers = _stream.SubscriberCount,
        };

        var eventId = _stream.Broadcast("server-tick", payload);
        _logger.LogDebug(
            "server-tick emitted: tick={Tick} eventId={EventId} subscribers={Subscribers}",
            tick,
            eventId,
            _stream.SubscriberCount);
    }
}
