// SPDX-License-Identifier: MIT
//
// Slice-4 verification tests for `RangeQueryService`. Locks:
//
//   * The validation rules (start > end → StartAfterEnd;
//     raw window > cap → RawWindowTooLarge).
//   * Cursor-clip semantics: an `end` past the cursor is silently
//     clamped to the cursor's `AsOf` (NOT a 400 — empty result instead).
//   * The "empty bucket fill" densification: aggregated requests
//     produce a contiguous bucket sequence even when the fetcher
//     returns only the populated rows.
//   * Raw requests pass rows through unchanged (no densification —
//     each row is its own bucket with `sampleCount: 1`).
//   * Heatmap densification: missing days appear with `sampleCount: 0`
//     and `temperatureMean: null`; leap years produce 366 cells.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;
using ClimaSense.Web.Readings;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class RangeQueryServiceTests
{
    private static CursorSnapshot _snap(DateTime asOf) => new(asOf);

    // -----------------------------------------------------------------
    // Fakes — capture inputs + return canned data.
    // -----------------------------------------------------------------
    private sealed class _RangeRecorder
    {
        public int Calls { get; private set; }
        public RangeBucket LastBucket { get; private set; }
        public DateTime LastStart { get; private set; }
        public DateTime LastEnd { get; private set; }
        public DateTime LastAsOf { get; private set; }

        public IReadOnlyList<BucketedReading> Result { get; set; } =
            Array.Empty<BucketedReading>();

        public Task<IReadOnlyList<BucketedReading>> Fetch(
            RangeBucket bucket,
            DateTime start,
            DateTime end,
            DateTime asOf,
            CancellationToken ct)
        {
            Calls += 1;
            LastBucket = bucket;
            LastStart = start;
            LastEnd = end;
            LastAsOf = asOf;
            return Task.FromResult(Result);
        }
    }

    private sealed class _HeatmapRecorder
    {
        public int Calls { get; private set; }
        public IReadOnlyList<HeatmapCell> Result { get; set; } =
            Array.Empty<HeatmapCell>();

        public Task<IReadOnlyList<HeatmapCell>> Fetch(
            DateTime yearStart,
            DateTime yearEnd,
            DateTime asOf,
            CancellationToken ct)
        {
            Calls += 1;
            return Task.FromResult(Result);
        }
    }

    private static RangeQueryService _service(
        _RangeRecorder? range = null,
        _HeatmapRecorder? heatmap = null,
        int rawMaxDays = RangeQueryService.DefaultRawMaxDays)
    {
        range ??= new _RangeRecorder();
        heatmap ??= new _HeatmapRecorder();
        return new RangeQueryService(range.Fetch, heatmap.Fetch, rawMaxDays);
    }

    // -----------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------
    [Fact]
    public void ValidateAndClip_rejects_start_after_end()
    {
        var svc = _service();
        var snap = _snap(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc));
        var args = new RangeQueryArgs(
            Start: new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
            End: new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc),
            Bucket: RangeBucket.Hour);

        var outcome = svc.ValidateAndClip(snap, args, out _, out _);

        Assert.Equal(RangeQueryError.StartAfterEnd, outcome);
    }

    [Fact]
    public void ValidateAndClip_rejects_raw_window_wider_than_cap()
    {
        var svc = _service(rawMaxDays: 7);
        var snap = _snap(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc));
        var args = new RangeQueryArgs(
            Start: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            End: new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc), // 9 days
            Bucket: RangeBucket.Raw);

        var outcome = svc.ValidateAndClip(snap, args, out _, out _);

        Assert.Equal(RangeQueryError.RawWindowTooLarge, outcome);
    }

    [Fact]
    public void ValidateAndClip_allows_raw_window_at_cap()
    {
        var svc = _service(rawMaxDays: 7);
        var snap = _snap(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc));
        var args = new RangeQueryArgs(
            Start: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            End: new DateTime(2026, 5, 8, 0, 0, 0, DateTimeKind.Utc), // 7 days exactly
            Bucket: RangeBucket.Raw);

        var outcome = svc.ValidateAndClip(snap, args, out _, out _);

        Assert.Equal(RangeQueryError.None, outcome);
    }

    [Fact]
    public void ValidateAndClip_clamps_end_past_cursor_to_asOf()
    {
        var svc = _service();
        var snap = _snap(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc));
        var args = new RangeQueryArgs(
            Start: new DateTime(2026, 5, 16, 0, 0, 0, DateTimeKind.Utc),
            End: new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc), // past cursor
            Bucket: RangeBucket.Hour);

        var outcome = svc.ValidateAndClip(snap, args, out _, out var end);

        Assert.Equal(RangeQueryError.None, outcome);
        Assert.Equal(snap.AsOf, end);
    }

    // -----------------------------------------------------------------
    // Range fetching + densification
    // -----------------------------------------------------------------
    [Fact]
    public async Task GetRange_invokes_fetcher_with_cursor_AsOf()
    {
        var rec = new _RangeRecorder();
        var svc = _service(range: rec);
        var snap = _snap(new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc));
        var args = new RangeQueryArgs(
            Start: new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc),
            End: new DateTime(2026, 5, 17, 3, 0, 0, DateTimeKind.Utc),
            Bucket: RangeBucket.Hour);

        await svc.GetRangeAsync(snap, args, CancellationToken.None);

        Assert.Equal(1, rec.Calls);
        Assert.Equal(RangeBucket.Hour, rec.LastBucket);
        Assert.Equal(snap.AsOf, rec.LastAsOf);
    }

    [Fact]
    public async Task GetRange_densifies_aggregated_buckets_filling_gaps()
    {
        // Hour bucket; ask for 4 hours but the fetcher returns only the
        // first and third. The service should fill in two empty buckets.
        // Range is half-open [start, end), so end = start + 4h gives 4 buckets.
        var start = new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 5, 17, 4, 0, 0, DateTimeKind.Utc);
        var rec = new _RangeRecorder
        {
            Result = new List<BucketedReading>
            {
                new(start,                    SampleCount: 12,
                    TemperatureMean: 21.0, TemperatureMin: 20.0, TemperatureMax: 22.0,
                    HumidityMean: 40.0,     HumidityMin: 38.0,     HumidityMax: 42.0),
                new(start.AddHours(2),        SampleCount: 11,
                    TemperatureMean: 22.0, TemperatureMin: 21.5, TemperatureMax: 22.5,
                    HumidityMean: 41.0,     HumidityMin: 40.0,     HumidityMax: 42.0),
            }
        };
        var svc = _service(range: rec);
        var snap = _snap(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc));

        var response = await svc.GetRangeAsync(
            snap,
            new RangeQueryArgs(start, end, RangeBucket.Hour),
            CancellationToken.None);

        Assert.Equal("hour", response.Bucket);
        Assert.Equal(4, response.Buckets.Count);            // 0,1,2,3 hours
        Assert.Equal(12, response.Buckets[0].SampleCount);
        Assert.Equal(0,  response.Buckets[1].SampleCount);  // empty
        Assert.Null(response.Buckets[1].TemperatureMean);
        Assert.Equal(11, response.Buckets[2].SampleCount);
        Assert.Equal(0,  response.Buckets[3].SampleCount);
    }

    [Fact]
    public async Task GetRange_raw_passes_rows_through_without_densification()
    {
        // Raw bucket: every fetched row is itself a "bucket" with
        // sampleCount=1; the service does not fill gaps.
        var rows = new List<BucketedReading>
        {
            new(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc),
                SampleCount: 1, TemperatureMean: 21.0, TemperatureMin: 21.0, TemperatureMax: 21.0,
                HumidityMean: 40.0, HumidityMin: 40.0, HumidityMax: 40.0),
            new(new DateTime(2026, 5, 17, 0, 5, 0, DateTimeKind.Utc),
                SampleCount: 1, TemperatureMean: 21.0, TemperatureMin: 21.0, TemperatureMax: 21.0,
                HumidityMean: 40.0, HumidityMin: 40.0, HumidityMax: 40.0),
        };
        var rec = new _RangeRecorder { Result = rows };
        var svc = _service(range: rec);
        var snap = _snap(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc));

        var response = await svc.GetRangeAsync(
            snap,
            new RangeQueryArgs(
                new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 17, 1, 0, 0, DateTimeKind.Utc),
                RangeBucket.Raw),
            CancellationToken.None);

        Assert.Equal("raw", response.Bucket);
        Assert.Equal(2, response.Buckets.Count);
        Assert.All(response.Buckets, b => Assert.Equal(1, b.SampleCount));
    }

    [Fact]
    public async Task GetRange_throws_if_validation_skipped_and_args_invalid()
    {
        var svc = _service();
        var snap = _snap(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc));
        var args = new RangeQueryArgs(
            Start: new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
            End: new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc),
            Bucket: RangeBucket.Hour);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.GetRangeAsync(snap, args, CancellationToken.None));
    }

    // -----------------------------------------------------------------
    // Heatmap
    // -----------------------------------------------------------------
    [Fact]
    public async Task GetHeatmap_emits_365_cells_for_non_leap_year()
    {
        var rec = new _HeatmapRecorder();
        var svc = _service(heatmap: rec);
        var snap = _snap(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc));

        var response = await svc.GetHeatmapAsync(snap, 2025, CancellationToken.None);

        Assert.Equal(2025, response.Year);
        Assert.Equal(365, response.Cells.Count);
        Assert.Equal(new DateOnly(2025, 1, 1), response.Cells[0].Date);
        Assert.Equal(new DateOnly(2025, 12, 31), response.Cells[^1].Date);
        Assert.All(response.Cells, c => Assert.Equal(0, c.SampleCount));
    }

    [Fact]
    public async Task GetHeatmap_emits_366_cells_for_leap_year()
    {
        var rec = new _HeatmapRecorder();
        var svc = _service(heatmap: rec);
        var snap = _snap(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc));

        var response = await svc.GetHeatmapAsync(snap, 2024, CancellationToken.None);

        Assert.Equal(366, response.Cells.Count);
        Assert.Contains(response.Cells, c => c.Date == new DateOnly(2024, 2, 29));
    }

    [Fact]
    public async Task GetHeatmap_merges_fetched_means_into_dense_grid()
    {
        var rec = new _HeatmapRecorder
        {
            Result = new List<HeatmapCell>
            {
                new(new DateOnly(2024, 1, 1),   SampleCount: 12, TemperatureMean: 19.5),
                new(new DateOnly(2024, 6, 15),  SampleCount: 24, TemperatureMean: 23.0),
                new(new DateOnly(2024, 12, 31), SampleCount: 8,  TemperatureMean: 4.5),
            }
        };
        var svc = _service(heatmap: rec);
        var snap = _snap(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc));

        var response = await svc.GetHeatmapAsync(snap, 2024, CancellationToken.None);

        var jan1 = response.Cells.First(c => c.Date == new DateOnly(2024, 1, 1));
        var jun15 = response.Cells.First(c => c.Date == new DateOnly(2024, 6, 15));
        var jun16 = response.Cells.First(c => c.Date == new DateOnly(2024, 6, 16));

        Assert.Equal(12, jan1.SampleCount);
        Assert.Equal(19.5, jan1.TemperatureMean);
        Assert.Equal(24, jun15.SampleCount);
        Assert.Equal(0, jun16.SampleCount);
        Assert.Null(jun16.TemperatureMean);
    }

    [Fact]
    public async Task GetHeatmap_rejects_out_of_range_years()
    {
        var svc = _service();
        var snap = _snap(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await svc.GetHeatmapAsync(snap, 1800, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await svc.GetHeatmapAsync(snap, 2300, CancellationToken.None));
    }

    // -----------------------------------------------------------------
    // Bucket-edge alignment
    // -----------------------------------------------------------------
    [Theory]
    [InlineData("2026-05-17T03:45:23Z", RangeBucket.Hour, "2026-05-17T03:00:00Z")]
    [InlineData("2026-05-17T03:45:23Z", RangeBucket.Day,  "2026-05-17T00:00:00Z")]
    public void AlignDown_matches_DATE_BUCKET_for_hour_and_day(
        string input, RangeBucket bucket, string expectedIso)
    {
        var inputUtc = DateTime.Parse(input, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var expected = DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var actual = RangeQueryService.AlignDown(inputUtc, bucket);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AlignDown_week_lands_on_monday()
    {
        // 2026-05-17 is a Sunday; previous Monday is 2026-05-11.
        var sunday = new DateTime(2026, 5, 17, 23, 59, 0, DateTimeKind.Utc);
        var monday = RangeQueryService.AlignDown(sunday, RangeBucket.Week);
        Assert.Equal(new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc), monday);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
    }

    // -----------------------------------------------------------------
    // SQL golden strings — locks the exact emission so a careless
    // refactor doesn't accidentally drop the cursor-clip clause.
    // -----------------------------------------------------------------
    [Fact]
    public void BuildAggregatedSql_includes_cursor_clip_and_date_bucket()
    {
        var sql = SqlRangeFetcher.BuildAggregatedSql(RangeBucket.Hour);
        Assert.Contains("DATE_BUCKET(HOUR, 1, ReadingTime)", sql);
        Assert.Contains("@start", sql);
        Assert.Contains("@end", sql);
        Assert.Contains("@asOf", sql);
        Assert.Contains("WHERE", sql);
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("ORDER BY BucketTime", sql);
    }

    [Fact]
    public void BuildRawSql_includes_cursor_clip()
    {
        var sql = SqlRangeFetcher.BuildRawSql();
        Assert.Contains("ReadingTime", sql);
        Assert.Contains("Temperature", sql);
        Assert.Contains("Humidity", sql);
        Assert.Contains("@start", sql);
        Assert.Contains("@end", sql);
        Assert.Contains("@asOf", sql);
        Assert.DoesNotContain("GROUP BY", sql);
    }

    // -----------------------------------------------------------------
    // Constructor guards
    // -----------------------------------------------------------------
    [Fact]
    public void Constructor_rejects_null_fetchers()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RangeQueryService(null!, (_, _, _, _) => Task.FromResult<IReadOnlyList<HeatmapCell>>(Array.Empty<HeatmapCell>())));
        Assert.Throws<ArgumentNullException>(() =>
            new RangeQueryService((_, _, _, _, _) => Task.FromResult<IReadOnlyList<BucketedReading>>(Array.Empty<BucketedReading>()), null!));
    }

    [Fact]
    public void Constructor_rejects_non_positive_raw_max_days()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RangeQueryService(
                (_, _, _, _, _) => Task.FromResult<IReadOnlyList<BucketedReading>>(Array.Empty<BucketedReading>()),
                (_, _, _, _) => Task.FromResult<IReadOnlyList<HeatmapCell>>(Array.Empty<HeatmapCell>()),
                rawMaxDays: 0));
    }
}
