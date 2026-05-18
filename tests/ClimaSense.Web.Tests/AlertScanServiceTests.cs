// SPDX-License-Identifier: MIT
//
// Slice-11 unit coverage for the threshold engine core
// (`AlertScanService`).
//
// Covers the four claims pinned by the brief:
//
//   * Closure-only: an open interval (BreachEnd == @asOf) is filtered
//     out by the engine's SQL — modelled here by the breach scanner
//     stub returning zero rows when the interval is still open. The
//     gaps-and-islands SQL shape is also pinned by golden-string
//     assertions.
//   * Idempotency: re-running the scanner against the same data
//     returns zero new alerts (the inserter delegate is the
//     authoritative seam; we return null on the second call to
//     simulate UNIQUE(RuleId, BreachStart) holding).
//   * SSE emission contract: every newly-inserted Alert produces
//     exactly one NewAlert in the returned list (the caller —
//     `ThresholdAlertScanner` — broadcasts one SSE frame per item).
//   * Cursor passthrough: @asOf used by both the breach scanner and
//     the inserter equals `CursorSnapshot.AsOf`.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Alerts;
using ClimaSense.Web.Cursor;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class AlertScanServiceTests
{
    private static readonly AlertRule HeatRule = new(
        RuleId: 1,
        Name: "Heat",
        Metric: "T",
        Operator: ">",
        Threshold: 26.0,
        DurationMinutes: 30,
        Enabled: true);

    private static readonly AlertRule DampRule = new(
        RuleId: 3,
        Name: "Damp",
        Metric: "RH",
        Operator: ">",
        Threshold: 70.0,
        DurationMinutes: 60,
        Enabled: true);

    private static CursorSnapshot Cursor(DateTime asOf) => new(asOf);

    // -----------------------------------------------------------------
    // Closure-only filter — pinned in the golden SQL string.
    // -----------------------------------------------------------------

    [Fact]
    public void RenderGapsAndIslandsSql_uses_closure_only_filter()
    {
        var sql = AlertScanService.RenderGapsAndIslandsSql(HeatRule);
        // The closure-only filter:
        //   AND MAX(ReadingTime) < @asOf
        // is the line that prevents an open interval from firing.
        Assert.Contains("MAX(ReadingTime) < @asOf", sql);
    }

    [Fact]
    public void RenderGapsAndIslandsSql_uses_duration_filter()
    {
        var sql = AlertScanService.RenderGapsAndIslandsSql(HeatRule);
        Assert.Contains(
            "DATEDIFF(MINUTE, MIN(ReadingTime), MAX(ReadingTime)) >= @durationMinutes",
            sql);
    }

    [Fact]
    public void RenderGapsAndIslandsSql_uses_window_bounds_and_threshold_parameters()
    {
        var sql = AlertScanService.RenderGapsAndIslandsSql(HeatRule);
        // Window bounds are parameterised so callers can't smuggle SQL.
        Assert.Contains("ReadingTime >= @windowStart", sql);
        Assert.Contains("ReadingTime <= @asOf", sql);
        Assert.Contains("@threshold", sql);
        Assert.Contains("@durationMinutes", sql);
    }

    [Fact]
    public void RenderGapsAndIslandsSql_uses_5_minute_gap_split()
    {
        // Gaps wider than 5 minutes split the run — locks the
        // "missing data shouldn't merge two separate breaches" claim.
        var sql = AlertScanService.RenderGapsAndIslandsSql(HeatRule);
        Assert.Contains(
            "DATEDIFF(MINUTE, PrevTime, ReadingTime) > 5", sql);
    }

    [Fact]
    public void RenderGapsAndIslandsSql_renders_temperature_predicate()
    {
        var sql = AlertScanService.RenderGapsAndIslandsSql(HeatRule);
        Assert.Contains("Temperature > @threshold", sql);
        Assert.Contains("MAX(Metric)", sql);  // peak aggregate for `>`.
    }

    [Fact]
    public void RenderGapsAndIslandsSql_renders_humidity_predicate()
    {
        var sql = AlertScanService.RenderGapsAndIslandsSql(DampRule);
        Assert.Contains("Humidity > @threshold", sql);
    }

    [Fact]
    public void RenderGapsAndIslandsSql_renders_min_peak_for_lt_rule()
    {
        var coldRule = new AlertRule(
            RuleId: 2, Name: "Cold", Metric: "T", Operator: "<",
            Threshold: 18.0, DurationMinutes: 60, Enabled: true);
        var sql = AlertScanService.RenderGapsAndIslandsSql(coldRule);
        Assert.Contains("Temperature < @threshold", sql);
        Assert.Contains("MIN(Metric)", sql);
    }

    [Fact]
    public void LookbackWindow_is_24_hours_per_brief()
    {
        Assert.Equal(TimeSpan.FromHours(24), AlertScanService.LookbackWindow);
    }

    // -----------------------------------------------------------------
    // Engine behaviour.
    // -----------------------------------------------------------------

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        AlertRulesLoader loader = _ => Task.FromResult<IReadOnlyList<AlertRule>>(
            Array.Empty<AlertRule>());
        RuleBreachScanner scanner = (r, t, ct) =>
            Task.FromResult<IReadOnlyList<BreachInterval>>(
                Array.Empty<BreachInterval>());
        AlertInserter inserter = (b, t, ct) => Task.FromResult<long?>(null);

        Assert.Throws<ArgumentNullException>(() =>
            new AlertScanService(null!, scanner, inserter));
        Assert.Throws<ArgumentNullException>(() =>
            new AlertScanService(loader, null!, inserter));
        Assert.Throws<ArgumentNullException>(() =>
            new AlertScanService(loader, scanner, null!));
    }

    [Fact]
    public async Task ScanOnceAsync_passes_cursor_AsOf_to_scanner_and_inserter()
    {
        var asOf = new DateTime(2026, 5, 17, 14, 30, 0, DateTimeKind.Utc);
        DateTime scanAsOf = default, insertAsOf = default;

        var service = new AlertScanService(
            rulesLoader: _ => Task.FromResult<IReadOnlyList<AlertRule>>(
                new[] { HeatRule }),
            breachScanner: (rule, t, ct) =>
            {
                scanAsOf = t;
                return Task.FromResult<IReadOnlyList<BreachInterval>>(new[]
                {
                    new BreachInterval(
                        RuleId: rule.RuleId,
                        BreachStart: t.AddHours(-2),
                        BreachEnd: t.AddHours(-1),
                        PeakValue: 27.5),
                });
            },
            alertInserter: (breach, replayClockAtFire, ct) =>
            {
                insertAsOf = replayClockAtFire;
                return Task.FromResult<long?>(42);
            });

        var emitted = await service.ScanOnceAsync(Cursor(asOf), CancellationToken.None);

        Assert.Equal(asOf, scanAsOf);
        Assert.Equal(asOf, insertAsOf);
        Assert.Single(emitted);
        Assert.Equal(42, emitted[0].AlertId);
        Assert.Equal(asOf, emitted[0].ReplayClockAtFire);
    }

    [Fact]
    public async Task ScanOnceAsync_skips_disabled_rules()
    {
        var disabled = HeatRule with { Enabled = false };
        var scannedAtLeastOnce = false;

        var service = new AlertScanService(
            rulesLoader: _ => Task.FromResult<IReadOnlyList<AlertRule>>(
                new[] { disabled }),
            breachScanner: (rule, t, ct) =>
            {
                scannedAtLeastOnce = true;
                return Task.FromResult<IReadOnlyList<BreachInterval>>(
                    Array.Empty<BreachInterval>());
            },
            alertInserter: (b, t, ct) => Task.FromResult<long?>(1));

        var emitted = await service.ScanOnceAsync(
            Cursor(DateTime.UtcNow), CancellationToken.None);

        Assert.False(scannedAtLeastOnce,
            "disabled rules must not be evaluated by the engine");
        Assert.Empty(emitted);
    }

    [Fact]
    public async Task ScanOnceAsync_idempotent_when_inserter_returns_null()
    {
        // Simulates UNIQUE(RuleId, BreachStart) holding — the inserter
        // returns null on every call. The engine MUST emit zero
        // NewAlert records (no toast, no duplicate row).
        var asOf = new DateTime(2026, 5, 17, 14, 30, 0, DateTimeKind.Utc);
        var service = new AlertScanService(
            rulesLoader: _ => Task.FromResult<IReadOnlyList<AlertRule>>(
                new[] { HeatRule }),
            breachScanner: (rule, t, ct) =>
                Task.FromResult<IReadOnlyList<BreachInterval>>(new[]
                {
                    new BreachInterval(rule.RuleId,
                        t.AddHours(-2), t.AddHours(-1), 27.5),
                }),
            alertInserter: (b, t, ct) => Task.FromResult<long?>(null));

        var emitted = await service.ScanOnceAsync(Cursor(asOf), CancellationToken.None);

        Assert.Empty(emitted);
    }

    [Fact]
    public async Task ScanOnceAsync_emits_one_NewAlert_per_inserted_row()
    {
        // Two breach intervals from one rule; the inserter returns
        // distinct ids for both. The engine must emit exactly two
        // NewAlert records, one per insertion.
        var asOf = new DateTime(2026, 5, 17, 14, 30, 0, DateTimeKind.Utc);
        var insertCount = 0;

        var service = new AlertScanService(
            rulesLoader: _ => Task.FromResult<IReadOnlyList<AlertRule>>(
                new[] { HeatRule }),
            breachScanner: (rule, t, ct) =>
                Task.FromResult<IReadOnlyList<BreachInterval>>(new[]
                {
                    new BreachInterval(rule.RuleId,
                        t.AddHours(-5), t.AddHours(-4.5), 27.0),
                    new BreachInterval(rule.RuleId,
                        t.AddHours(-2), t.AddHours(-1), 28.5),
                }),
            alertInserter: (b, t, ct) =>
            {
                insertCount++;
                return Task.FromResult<long?>(100 + insertCount);
            });

        var emitted = await service.ScanOnceAsync(Cursor(asOf), CancellationToken.None);

        Assert.Equal(2, emitted.Count);
        Assert.Equal(101, emitted[0].AlertId);
        Assert.Equal(102, emitted[1].AlertId);
        // SSE payload composition — every NewAlert must carry the
        // rule reference so the broadcaster can render `ruleName` +
        // `ruleSummary` without a second lookup.
        Assert.Same(HeatRule, emitted[0].Rule);
        Assert.Same(HeatRule, emitted[1].Rule);
    }

    [Fact]
    public async Task ScanOnceAsync_scans_each_enabled_rule_exactly_once()
    {
        var rules = new List<AlertRule>
        {
            HeatRule,
            DampRule,
            HeatRule with { RuleId = 99, Enabled = false },  // skipped.
        };
        var scanCalls = new List<int>();

        var service = new AlertScanService(
            rulesLoader: _ => Task.FromResult<IReadOnlyList<AlertRule>>(rules),
            breachScanner: (rule, t, ct) =>
            {
                scanCalls.Add(rule.RuleId);
                return Task.FromResult<IReadOnlyList<BreachInterval>>(
                    Array.Empty<BreachInterval>());
            },
            alertInserter: (b, t, ct) => Task.FromResult<long?>(1));

        await service.ScanOnceAsync(
            Cursor(DateTime.UtcNow), CancellationToken.None);

        Assert.Equal(new[] { 1, 3 }, scanCalls);
    }

    [Fact]
    public async Task ScanOnceAsync_throws_on_null_cursor()
    {
        var service = new AlertScanService(
            _ => Task.FromResult<IReadOnlyList<AlertRule>>(Array.Empty<AlertRule>()),
            (r, t, ct) => Task.FromResult<IReadOnlyList<BreachInterval>>(
                Array.Empty<BreachInterval>()),
            (b, t, ct) => Task.FromResult<long?>(null));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.ScanOnceAsync(null!, CancellationToken.None));
    }
}
