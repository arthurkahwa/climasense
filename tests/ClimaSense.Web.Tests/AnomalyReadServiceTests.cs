// SPDX-License-Identifier: MIT
//
// Slice-8 verification tests for `AnomalyReadService`. Locks:
//
//   * `GetLatestAsync` calls the injected fetcher exactly once,
//     passing `cursor.AsOf`.
//   * `GetRangeAsync` defaults to `[cursor - 24h, cursor]` when
//     start/end are omitted.
//   * Range cap rejects windows wider than 90 days.
//   * `start > end` rejected.
//   * Pinned SQL targets the `dbo.fv_anomalies_at_cursor` TVF and
//     orders by `DetectedAt DESC TOP 1` for latest, by
//     `ReadingTime DESC` for range — guards against a refactor that
//     drops the cursor-clip or the ordering.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Anomalies;
using ClimaSense.Web.Cursor;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class AnomalyReadServiceTests
{
    private sealed class _LatestRecorder
    {
        public int Calls { get; private set; }
        public DateTime LastAsOf { get; private set; }
        public LatestAnomalyDto? Returns { get; set; }

        public Task<LatestAnomalyDto?> Fetch(DateTime asOf, CancellationToken ct)
        {
            Calls += 1;
            LastAsOf = asOf;
            return Task.FromResult(Returns);
        }
    }

    private sealed class _RangeRecorder
    {
        public int Calls { get; private set; }
        public DateTime LastAsOf { get; private set; }
        public DateTime LastStart { get; private set; }
        public DateTime LastEnd { get; private set; }
        public string? LastType { get; private set; }
        public IReadOnlyList<LatestAnomalyDto> Returns { get; set; } =
            Array.Empty<LatestAnomalyDto>();

        public Task<IReadOnlyList<LatestAnomalyDto>> Fetch(
            DateTime asOf,
            DateTime start,
            DateTime end,
            string? anomalyType,
            CancellationToken ct)
        {
            Calls += 1;
            LastAsOf = asOf;
            LastStart = start;
            LastEnd = end;
            LastType = anomalyType;
            return Task.FromResult(Returns);
        }
    }

    private static CursorSnapshot _CursorAt(DateTime ts) =>
        new CursorSnapshot(DateTime.SpecifyKind(ts, DateTimeKind.Utc));

    [Fact]
    public async Task GetLatest_calls_fetcher_with_cursor_asof()
    {
        var rec = new _LatestRecorder
        {
            Returns = new LatestAnomalyDto(
                AnomalyType: "sensor_failure",
                ReadingTime: new DateTime(2026, 5, 17, 10, 0, 0, DateTimeKind.Utc),
                Severity: 1.0,
                Description: "gap 12 min",
                DetectedAt: new DateTime(2026, 5, 17, 10, 0, 5, DateTimeKind.Utc)),
        };
        var svc = new AnomalyReadService(
            rec.Fetch,
            (asOf, s, e, t, ct) => Task.FromResult<IReadOnlyList<LatestAnomalyDto>>(
                Array.Empty<LatestAnomalyDto>()));
        var cursor = _CursorAt(new DateTime(2026, 5, 17, 12, 0, 0));

        var result = await svc.GetLatestAsync(cursor, CancellationToken.None);

        Assert.Equal(1, rec.Calls);
        Assert.Equal(cursor.AsOf, rec.LastAsOf);
        Assert.NotNull(result);
        Assert.Equal("sensor_failure", result!.AnomalyType);
    }

    [Fact]
    public async Task GetLatest_returns_null_when_table_empty()
    {
        var rec = new _LatestRecorder { Returns = null };
        var svc = new AnomalyReadService(
            rec.Fetch,
            (asOf, s, e, t, ct) => Task.FromResult<IReadOnlyList<LatestAnomalyDto>>(
                Array.Empty<LatestAnomalyDto>()));
        var cursor = _CursorAt(new DateTime(2026, 5, 17, 12, 0, 0));

        var result = await svc.GetLatestAsync(cursor, CancellationToken.None);

        Assert.Equal(1, rec.Calls);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetRange_defaults_to_24h_lookback_at_cursor()
    {
        var rec = new _RangeRecorder();
        var svc = new AnomalyReadService(
            (asOf, ct) => Task.FromResult<LatestAnomalyDto?>(null),
            rec.Fetch);
        var cursor = _CursorAt(new DateTime(2026, 5, 17, 12, 0, 0));

        var response = await svc.GetRangeAsync(
            cursor, start: null, end: null, anomalyType: null,
            CancellationToken.None);

        Assert.Equal(1, rec.Calls);
        Assert.Equal(cursor.AsOf, rec.LastEnd);
        Assert.Equal(cursor.AsOf - TimeSpan.FromHours(24), rec.LastStart);
        Assert.Null(rec.LastType);
        Assert.Equal(cursor.AsOf, response.End);
    }

    [Fact]
    public async Task GetRange_passes_type_filter_through()
    {
        var rec = new _RangeRecorder();
        var svc = new AnomalyReadService(
            (asOf, ct) => Task.FromResult<LatestAnomalyDto?>(null),
            rec.Fetch);
        var cursor = _CursorAt(new DateTime(2026, 5, 17, 12, 0, 0));

        var response = await svc.GetRangeAsync(
            cursor,
            start: cursor.AsOf - TimeSpan.FromHours(6),
            end: cursor.AsOf,
            anomalyType: "regime_shift",
            CancellationToken.None);

        Assert.Equal("regime_shift", rec.LastType);
        Assert.Equal("regime_shift", response.Type);
    }

    [Fact]
    public async Task GetRange_rejects_start_after_end()
    {
        var rec = new _RangeRecorder();
        var svc = new AnomalyReadService(
            (asOf, ct) => Task.FromResult<LatestAnomalyDto?>(null),
            rec.Fetch);
        var cursor = _CursorAt(new DateTime(2026, 5, 17, 12, 0, 0));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await svc.GetRangeAsync(
                cursor,
                start: cursor.AsOf,
                end: cursor.AsOf - TimeSpan.FromHours(1),
                anomalyType: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task GetRange_rejects_window_wider_than_90_days()
    {
        var rec = new _RangeRecorder();
        var svc = new AnomalyReadService(
            (asOf, ct) => Task.FromResult<LatestAnomalyDto?>(null),
            rec.Fetch);
        var cursor = _CursorAt(new DateTime(2026, 5, 17, 12, 0, 0));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await svc.GetRangeAsync(
                cursor,
                start: cursor.AsOf - TimeSpan.FromDays(91),
                end: cursor.AsOf,
                anomalyType: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task GetLatest_rejects_null_cursor()
    {
        var svc = new AnomalyReadService(
            (asOf, ct) => Task.FromResult<LatestAnomalyDto?>(null),
            (asOf, s, e, t, ct) => Task.FromResult<IReadOnlyList<LatestAnomalyDto>>(
                Array.Empty<LatestAnomalyDto>()));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await svc.GetLatestAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void Constructor_rejects_null_fetcher()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AnomalyReadService(
                null!,
                (asOf, s, e, t, ct) => Task.FromResult<IReadOnlyList<LatestAnomalyDto>>(
                    Array.Empty<LatestAnomalyDto>())));
        Assert.Throws<ArgumentNullException>(() =>
            new AnomalyReadService(
                (asOf, ct) => Task.FromResult<LatestAnomalyDto?>(null),
                null!));
    }

    [Fact]
    public void SqlAnomalyFetcher_latest_query_targets_cursor_tvf_and_orders_by_detectedat()
    {
        // Pins the AC: "Dashboard's last-anomaly card shows the row
        // with the largest DetectedAt visible at the cursor."
        Assert.Contains("fv_anomalies_at_cursor", SqlAnomalyFetcher.LatestSql);
        Assert.Contains("@asOf", SqlAnomalyFetcher.LatestSql);
        Assert.Contains("AnomalyType", SqlAnomalyFetcher.LatestSql);
        Assert.Contains("ReadingTime", SqlAnomalyFetcher.LatestSql);
        Assert.Contains("Severity", SqlAnomalyFetcher.LatestSql);
        Assert.Contains("Description", SqlAnomalyFetcher.LatestSql);
        Assert.Contains("DetectedAt", SqlAnomalyFetcher.LatestSql);
        Assert.Contains("TOP 1", SqlAnomalyFetcher.LatestSql);
        Assert.Contains("ORDER BY DetectedAt DESC", SqlAnomalyFetcher.LatestSql);
    }

    [Fact]
    public void SqlAnomalyFetcher_range_query_carries_type_filter_and_orders_by_readingtime()
    {
        Assert.Contains("fv_anomalies_at_cursor", SqlAnomalyFetcher.RangeSql);
        Assert.Contains("@asOf", SqlAnomalyFetcher.RangeSql);
        Assert.Contains("@start", SqlAnomalyFetcher.RangeSql);
        Assert.Contains("@end", SqlAnomalyFetcher.RangeSql);
        Assert.Contains("@anomalyTypeFilter IS NULL", SqlAnomalyFetcher.RangeSql);
        Assert.Contains("AnomalyType = @anomalyTypeFilter", SqlAnomalyFetcher.RangeSql);
        Assert.Contains("ORDER BY ReadingTime DESC", SqlAnomalyFetcher.RangeSql);
    }
}
