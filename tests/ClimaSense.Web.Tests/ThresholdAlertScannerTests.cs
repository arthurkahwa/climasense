// SPDX-License-Identifier: MIT
//
// Slice-11 unit coverage for `ThresholdAlertScanner` — the
// BackgroundService that wires the scan loop into the SSE broadcaster.
//
// Covers:
//
//   * One TickOnceAsync() over a single new breach produces exactly
//     one `breach-detected` SSE frame with the canonical payload.
//   * Idempotent re-tick: the inserter returns null on the second
//     call (UNIQUE held), and zero new SSE frames are emitted.
//   * Resilience: a tick that throws inside the scanner does NOT
//     crash the BackgroundService (logged + swallowed).
//   * Cadence + initial delay constants reflect the brief.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Alerts;
using ClimaSense.Web.Clock;
using ClimaSense.Web.Sse;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class ThresholdAlertScannerTests
{
    private sealed class FixedClock : IClock
    {
        public DateTime Now { get; set; } =
            new(2026, 5, 17, 14, 30, 0, DateTimeKind.Utc);
        public DateTime UtcNow() => Now;
    }

    private sealed class FrameCollector
    {
        public ConcurrentBag<(string EventType, string DataJson, long EventId)> Frames { get; } = new();
    }

    /// <summary>
    /// Bridge to capture broadcast frames without spinning a real HTTP
    /// SSE client. We subscribe to the stream, broadcast, then drain
    /// what the subscriber received.
    /// </summary>
    private static async Task<(int FrameCount, List<string> EventTypes, List<string> Payloads)>
        DrainAsync(AlertStream stream, int expectedFrames, TimeSpan timeout)
    {
        using var subscription = stream.Subscribe();
        var types = new List<string>();
        var payloads = new List<string>();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var frame in subscription.Reader.ReadAllAsync(cts.Token))
            {
                types.Add(frame.EventType);
                payloads.Add(frame.DataJson);
                if (types.Count >= expectedFrames) break;
            }
        }
        catch (OperationCanceledException) { /* drain timeout */ }

        return (types.Count, types, payloads);
    }

    private static AlertScanService BuildScanService(
        AlertRule rule,
        BreachInterval breach,
        Func<long?> nextInsertId)
    {
        AlertRulesLoader loader = _ =>
            Task.FromResult<IReadOnlyList<AlertRule>>(new[] { rule });
        RuleBreachScanner scanner = (r, t, ct) =>
            Task.FromResult<IReadOnlyList<BreachInterval>>(new[] { breach });
        AlertInserter inserter = (b, t, ct) =>
            Task.FromResult<long?>(nextInsertId());
        return new AlertScanService(loader, scanner, inserter);
    }

    private static ServiceProvider BuildServices(AlertScanService scanSvc)
    {
        var services = new ServiceCollection();
        services.AddScoped<AlertScanService>(_ => scanSvc);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Cadence_is_one_wall_minute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ThresholdAlertScanner.Cadence);
    }

    [Fact]
    public void DefaultInitialDelay_is_30_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30),
            ThresholdAlertScanner.DefaultInitialDelay);
    }

    [Fact]
    public async Task TickOnceAsync_emits_one_breach_detected_frame_per_inserted_row()
    {
        var rule = new AlertRule(
            RuleId: 1, Name: "Heat", Metric: "T", Operator: ">",
            Threshold: 26.0, DurationMinutes: 30, Enabled: true);
        var breach = new BreachInterval(
            RuleId: 1,
            BreachStart: new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc),
            BreachEnd: new DateTime(2026, 5, 17, 9, 35, 0, DateTimeKind.Utc),
            PeakValue: 27.5);

        var scanSvc = BuildScanService(rule, breach, () => 99);
        using var sp = BuildServices(scanSvc);

        var stream = new AlertStream();
        var clock = new FixedClock();

        var scanner = new ThresholdAlertScanner(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            stream: stream,
            clock: clock,
            logger: NullLogger<ThresholdAlertScanner>.Instance,
            initialDelay: TimeSpan.Zero);

        // Subscribe BEFORE the tick so we capture the broadcast.
        using var subscription = stream.Subscribe();
        await scanner.TickOnceAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var receivedTypes = new List<string>();
        var receivedJson = new List<string>();
        var receivedIds = new List<long>();
        try
        {
            await foreach (var frame in subscription.Reader.ReadAllAsync(cts.Token))
            {
                receivedTypes.Add(frame.EventType);
                receivedJson.Add(frame.DataJson);
                receivedIds.Add(frame.EventId);
                break;
            }
        }
        catch (OperationCanceledException) { /* drain timeout */ }

        Assert.Single(receivedTypes);
        Assert.Equal("breach-detected", receivedTypes[0]);

        // Payload shape — camelCase keys for every field on
        // BreachDetectedPayload.
        var json = receivedJson[0];
        Assert.Contains("\"alertId\":99", json);
        Assert.Contains("\"ruleId\":1", json);
        Assert.Contains("\"ruleName\":\"Heat\"", json);
        Assert.Contains("\"ruleSummary\":", json);
        Assert.Contains("\"breachStart\":", json);
        Assert.Contains("\"breachEnd\":", json);
        Assert.Contains("\"peakValue\":27.5", json);
        Assert.Contains("\"replayClockAtFire\":", json);

        // Event id is on the monotonic id space.
        Assert.True(receivedIds[0] >= 1,
            "breach-detected event must have a non-zero monotonic id");
    }

    [Fact]
    public async Task TickOnceAsync_emits_zero_frames_on_idempotent_redetection()
    {
        var rule = new AlertRule(
            RuleId: 1, Name: "Heat", Metric: "T", Operator: ">",
            Threshold: 26.0, DurationMinutes: 30, Enabled: true);
        var breach = new BreachInterval(
            RuleId: 1,
            BreachStart: new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc),
            BreachEnd: new DateTime(2026, 5, 17, 9, 35, 0, DateTimeKind.Utc),
            PeakValue: 27.5);

        var insertCallCount = 0;
        var scanSvc = BuildScanService(rule, breach, () =>
        {
            insertCallCount++;
            // First call → row inserted; subsequent calls → UNIQUE
            // held, scanner returns null.
            return insertCallCount == 1 ? 7 : (long?)null;
        });
        using var sp = BuildServices(scanSvc);

        var stream = new AlertStream();
        var scanner = new ThresholdAlertScanner(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            stream: stream,
            clock: new FixedClock(),
            logger: NullLogger<ThresholdAlertScanner>.Instance,
            initialDelay: TimeSpan.Zero);

        // First tick emits one frame; second tick must emit zero.
        using var subscription = stream.Subscribe();

        await scanner.TickOnceAsync(CancellationToken.None);
        await scanner.TickOnceAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var receivedCount = 0;
        try
        {
            await foreach (var _ in subscription.Reader.ReadAllAsync(cts.Token))
            {
                receivedCount++;
            }
        }
        catch (OperationCanceledException) { /* drain timeout */ }

        Assert.Equal(1, receivedCount);
        Assert.Equal(2, insertCallCount);
    }

    [Fact]
    public async Task TickOnceAsync_swallows_inner_exceptions()
    {
        AlertRulesLoader loader = _ =>
            Task.FromException<IReadOnlyList<AlertRule>>(
                new InvalidOperationException("simulated DB outage"));
        RuleBreachScanner scanner = (r, t, ct) =>
            Task.FromResult<IReadOnlyList<BreachInterval>>(
                Array.Empty<BreachInterval>());
        AlertInserter inserter = (b, t, ct) =>
            Task.FromResult<long?>(null);
        var scanSvc = new AlertScanService(loader, scanner, inserter);

        var services = new ServiceCollection();
        services.AddScoped<AlertScanService>(_ => scanSvc);
        using var sp = services.BuildServiceProvider();

        var ts = new ThresholdAlertScanner(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            stream: new AlertStream(),
            clock: new FixedClock(),
            logger: NullLogger<ThresholdAlertScanner>.Instance,
            initialDelay: TimeSpan.Zero);

        // The brief: a failing tick must NOT bubble; the next wall-
        // minute gets another shot. The test passes iff this call
        // does not throw.
        await ts.TickOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TickOnceAsync_rethrows_when_cancellation_requested()
    {
        // OperationCanceledException is intentionally NOT swallowed —
        // BackgroundService cancellation must propagate.
        AlertRulesLoader loader = ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AlertRule>>(Array.Empty<AlertRule>());
        };
        var scanSvc = new AlertScanService(
            loader,
            (r, t, ct) => Task.FromResult<IReadOnlyList<BreachInterval>>(Array.Empty<BreachInterval>()),
            (b, t, ct) => Task.FromResult<long?>(null));

        var services = new ServiceCollection();
        services.AddScoped<AlertScanService>(_ => scanSvc);
        using var sp = services.BuildServiceProvider();

        var ts = new ThresholdAlertScanner(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            stream: new AlertStream(),
            clock: new FixedClock(),
            logger: NullLogger<ThresholdAlertScanner>.Instance,
            initialDelay: TimeSpan.Zero);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ts.TickOnceAsync(cts.Token));
    }
}
