// SPDX-License-Identifier: MIT
//
// ThresholdAlertScanner — slice-11 wall-minute scheduler.
//
// `BackgroundService` firing once per wall-minute via `PeriodicTimer`,
// the .NET-equivalent of the APScheduler `interval` job described in
// epic #2. Each tick:
//
//   1. Captures a `CursorSnapshot` from `IClock` (read once per tick).
//   2. Calls `AlertScanService.ScanOnceAsync(cursor, ct)` which runs
//      the per-rule gaps-and-islands SQL and idempotently inserts any
//      closed breach interval not already present.
//   3. For every row that actually landed, broadcasts an SSE
//      `event: breach-detected` frame via the slice-1 `AlertStream`.
//
// Idempotency holds across both the SQL layer (UNIQUE(RuleId,
// BreachStart) + WHERE NOT EXISTS) and the SSE layer (only the rows
// that *actually* inserted produce an event — re-detections are
// silent no-ops).

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Clock;
using ClimaSense.Web.Cursor;
using ClimaSense.Web.Sse;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClimaSense.Web.Alerts;

public sealed class ThresholdAlertScanner : BackgroundService
{
    /// <summary>
    /// Wall-time cadence. Epic #2 pins "every wall-minute"; we honour
    /// that by firing every 60 wall-seconds regardless of cursor
    /// speed (replay multiplier is internal to `ScanOnceAsync`).
    /// </summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Startup delay. We wait one cadence period before the first
    /// scan so the web tier has a chance to settle after boot
    /// (otherwise the very-first tick can race against the bootstrap
    /// completing in the ml tier). Override via
    /// <c>CLIMASENSE_ALERT_SCAN_INITIAL_DELAY_SECONDS</c>.
    /// </summary>
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AlertStream _stream;
    private readonly IClock _clock;
    private readonly ILogger<ThresholdAlertScanner> _logger;
    private readonly TimeSpan _initialDelay;

    public ThresholdAlertScanner(
        IServiceScopeFactory scopeFactory,
        AlertStream stream,
        IClock clock,
        ILogger<ThresholdAlertScanner> logger,
        TimeSpan? initialDelay = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _stream = stream;
        _clock = clock;
        _logger = logger;
        _initialDelay = initialDelay ?? DefaultInitialDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_initialDelay > TimeSpan.Zero)
            {
                await Task.Delay(_initialDelay, stoppingToken).ConfigureAwait(false);
            }

            // Run once immediately so demos don't wait a full minute.
            await TickOnceAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(Cadence);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    /// <summary>
    /// Public so the integration tests can drive a single tick without
    /// spinning the PeriodicTimer.
    /// </summary>
    public async Task TickOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var scanner = scope.ServiceProvider
                .GetRequiredService<AlertScanService>();
            var cursor = CursorSnapshot.FromClock(_clock);
            var emitted = await scanner.ScanOnceAsync(cursor, cancellationToken)
                .ConfigureAwait(false);

            foreach (var alert in emitted)
            {
                var payload = new BreachDetectedPayload(
                    AlertId: alert.AlertId,
                    RuleId: alert.Rule.RuleId,
                    RuleName: alert.Rule.Name,
                    RuleSummary: alert.Rule.Summary,
                    BreachStart: alert.Breach.BreachStart,
                    BreachEnd: alert.Breach.BreachEnd,
                    PeakValue: alert.Breach.PeakValue,
                    ReplayClockAtFire: alert.ReplayClockAtFire);
                var eventId = _stream.Broadcast("breach-detected", payload);
                _logger.LogInformation(
                    "breach-detected emitted: alertId={AlertId} ruleId={RuleId} eventId={EventId}",
                    alert.AlertId,
                    alert.Rule.RuleId,
                    eventId);
            }

            if (emitted.Count == 0)
            {
                _logger.LogDebug(
                    "ThresholdAlertScanner tick: asOf={AsOf} no new breaches",
                    cursor.AsOf);
            }
            else
            {
                _logger.LogInformation(
                    "ThresholdAlertScanner tick: asOf={AsOf} inserted {Count} new alert(s)",
                    cursor.AsOf,
                    emitted.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Per the brief: a failing tick must NOT take the
            // BackgroundService down — the next wall-minute tick
            // gets another shot. We surface the error in the log
            // and continue.
            _logger.LogWarning(ex, "ThresholdAlertScanner tick failed");
        }
    }
}
