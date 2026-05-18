// SPDX-License-Identifier: MIT
//
// Slice-10 unit coverage for `ComfortBudgetReadService` — defaults,
// null handling, threshold passthrough, window construction, and
// pinned SQL shape from `SqlComfortBudgetFetcher`.
//
// The pinned SQL strings cover all three aggregations:
//
//   1. HoursOutsideZoneSql — cursor-clipped via the comfort TVF,
//      window-bounded, threshold-parameterised.
//   2. WorstCellSql        — cursor-clipped via the day-profile TVF,
//      `TOP 1 ORDER BY MeanResidual ASC` (most-negative wins).
//   3. TrendSql            — daily aggregate via DATE_BUCKET, grouped
//      and ordered ascending.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Comfort;
using ClimaSense.Web.Cursor;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class ComfortBudgetReadServiceTests
{
    private static CursorSnapshot MakeCursor(DateTime asOf) =>
        new(DateTime.SpecifyKind(asOf, DateTimeKind.Utc));

    private static ComfortBudgetDto EmptyBudget(
        DateTime asOf,
        DateTime windowStart,
        DateTime windowEnd,
        int windowDays,
        double threshold) =>
        new(
            HoursOutsideZone: 0,
            Threshold: threshold,
            WindowDays: windowDays,
            WindowStart: windowStart,
            WindowEnd: windowEnd,
            WorstCell: null,
            Trend: Array.Empty<ComfortTrendPointDto>());

    [Fact]
    public void Constructor_rejects_null_fetcher()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ComfortBudgetReadService(null!));
    }

    [Fact]
    public void Constructor_rejects_negative_threshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ComfortBudgetReadService(
                (a, s, e, w, t, ct) =>
                    Task.FromResult(EmptyBudget(a, s, e, w, t)),
                threshold: -1.0));
    }

    [Fact]
    public void Constructor_rejects_threshold_over_100()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ComfortBudgetReadService(
                (a, s, e, w, t, ct) =>
                    Task.FromResult(EmptyBudget(a, s, e, w, t)),
                threshold: 101.0));
    }

    [Fact]
    public void Constructor_rejects_zero_window()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ComfortBudgetReadService(
                (a, s, e, w, t, ct) =>
                    Task.FromResult(EmptyBudget(a, s, e, w, t)),
                windowDays: 0));
    }

    [Fact]
    public void Default_threshold_is_70()
    {
        var svc = new ComfortBudgetReadService(
            (a, s, e, w, t, ct) =>
                Task.FromResult(EmptyBudget(a, s, e, w, t)));
        Assert.Equal(70.0, svc.Threshold);
        Assert.Equal(70.0, ComfortBudgetReadService.DefaultThreshold);
    }

    [Fact]
    public void Default_window_is_7_days()
    {
        var svc = new ComfortBudgetReadService(
            (a, s, e, w, t, ct) =>
                Task.FromResult(EmptyBudget(a, s, e, w, t)));
        Assert.Equal(7, svc.WindowDays);
        Assert.Equal(7, ComfortBudgetReadService.DefaultWindowDays);
    }

    [Fact]
    public async Task GetAsync_null_cursor_throws()
    {
        var svc = new ComfortBudgetReadService(
            (a, s, e, w, t, ct) =>
                Task.FromResult(EmptyBudget(a, s, e, w, t)));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            svc.GetAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_passes_cursor_and_default_window_to_fetcher()
    {
        DateTime capturedAsOf = default;
        DateTime capturedStart = default;
        DateTime capturedEnd = default;
        int capturedWindow = default;
        double capturedThreshold = default;
        var svc = new ComfortBudgetReadService(
            (a, s, e, w, t, ct) =>
            {
                capturedAsOf = a;
                capturedStart = s;
                capturedEnd = e;
                capturedWindow = w;
                capturedThreshold = t;
                return Task.FromResult(EmptyBudget(a, s, e, w, t));
            });

        var asOf = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
        await svc.GetAsync(MakeCursor(asOf), CancellationToken.None);

        Assert.Equal(asOf, capturedAsOf);
        Assert.Equal(asOf, capturedEnd);
        Assert.Equal(asOf.AddDays(-7), capturedStart);
        Assert.Equal(7, capturedWindow);
        Assert.Equal(70.0, capturedThreshold);
    }

    [Fact]
    public async Task GetAsync_passes_custom_threshold_and_window()
    {
        int capturedWindow = default;
        double capturedThreshold = default;
        var svc = new ComfortBudgetReadService(
            (a, s, e, w, t, ct) =>
            {
                capturedWindow = w;
                capturedThreshold = t;
                return Task.FromResult(EmptyBudget(a, s, e, w, t));
            },
            threshold: 80.0,
            windowDays: 14);

        var asOf = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
        await svc.GetAsync(MakeCursor(asOf), CancellationToken.None);

        Assert.Equal(80.0, capturedThreshold);
        Assert.Equal(14, capturedWindow);
    }

    [Fact]
    public async Task GetAsync_changing_threshold_changes_count_of_hours_outside_zone()
    {
        // Locks AC: "Changing COMFORT_DISCOMFORT_THRESHOLD to 80
        // increases the 'hours outside zone' count." The service
        // forwards the threshold to the fetcher verbatim; a fetcher
        // returning a function of the threshold demonstrates the AC.
        ComfortBudgetFetcher fetcher = (a, s, e, w, t, ct) =>
        {
            // Synthetic: 10 rows < 70, 20 rows < 80 (one stub per
            // threshold value).
            var hours = t >= 80.0 ? 20 : 10;
            return Task.FromResult(new ComfortBudgetDto(
                HoursOutsideZone: hours,
                Threshold: t,
                WindowDays: w,
                WindowStart: s,
                WindowEnd: e,
                WorstCell: null,
                Trend: Array.Empty<ComfortTrendPointDto>()));
        };
        var asOf = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);

        var svc70 = new ComfortBudgetReadService(fetcher, threshold: 70.0);
        var svc80 = new ComfortBudgetReadService(fetcher, threshold: 80.0);

        var budget70 = await svc70.GetAsync(MakeCursor(asOf), CancellationToken.None);
        var budget80 = await svc80.GetAsync(MakeCursor(asOf), CancellationToken.None);

        Assert.Equal(10, budget70.HoursOutsideZone);
        Assert.Equal(20, budget80.HoursOutsideZone);
        Assert.True(budget80.HoursOutsideZone > budget70.HoursOutsideZone);
    }

    [Fact]
    public async Task GetAsync_returns_fetcher_result_verbatim()
    {
        var worst = new WorstCalendarCellDto(
            Date: new DateOnly(2026, 5, 14),
            DayOfWeek: 3,
            MeanResidual: -1.83,
            MaxAbsZscore: 3.10,
            Pattern: "cool");
        var trend = new List<ComfortTrendPointDto>
        {
            new(new DateOnly(2026, 5, 15), 60.0, 95.0, 81.2, 24),
            new(new DateOnly(2026, 5, 16), 55.0, 90.0, 75.0, 24),
            new(new DateOnly(2026, 5, 17), 50.0, 88.0, 70.5, 12),
        };
        var asOf = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
        var budget = new ComfortBudgetDto(
            HoursOutsideZone: 7,
            Threshold: 70.0,
            WindowDays: 7,
            WindowStart: asOf.AddDays(-7),
            WindowEnd: asOf,
            WorstCell: worst,
            Trend: trend);

        var svc = new ComfortBudgetReadService(
            (a, s, e, w, t, ct) => Task.FromResult(budget));

        var result = await svc.GetAsync(MakeCursor(asOf), CancellationToken.None);

        Assert.Equal(7, result.HoursOutsideZone);
        Assert.NotNull(result.WorstCell);
        Assert.Equal(-1.83, result.WorstCell!.MeanResidual);
        Assert.Equal("cool", result.WorstCell.Pattern);
        Assert.Equal(3, result.Trend.Count);
        Assert.Equal(81.2, result.Trend[0].MeanScore);
    }

    // -----------------------------------------------------------------
    // Golden-string SQL tests — three aggregations, one assertion
    // block each. These lock the cursor-clipping invariants per the
    // epic + ADR-0011: derived tables clip via TVFs, never via
    // caller-side `WHERE`.
    // -----------------------------------------------------------------

    [Fact]
    public void HoursOutsideZoneSql_targets_comfort_tvf_and_thresholds()
    {
        Assert.Contains(
            "dbo.fv_comfortscores_at_cursor(@asOf)",
            SqlComfortBudgetFetcher.HoursOutsideZoneSql);
        Assert.Contains("COUNT(*)", SqlComfortBudgetFetcher.HoursOutsideZoneSql);
        Assert.Contains("Score < @threshold", SqlComfortBudgetFetcher.HoursOutsideZoneSql);
        Assert.Contains("BucketTime >= @start", SqlComfortBudgetFetcher.HoursOutsideZoneSql);
        Assert.Contains("BucketTime <= @end", SqlComfortBudgetFetcher.HoursOutsideZoneSql);
    }

    [Fact]
    public void WorstCellSql_targets_dayprofile_tvf_and_orders_by_residual_asc()
    {
        Assert.Contains(
            "dbo.fv_dayprofiles_at_cursor(@asOf)",
            SqlComfortBudgetFetcher.WorstCellSql);
        Assert.Contains("TOP 1", SqlComfortBudgetFetcher.WorstCellSql);
        Assert.Contains("ORDER BY MeanResidual ASC", SqlComfortBudgetFetcher.WorstCellSql);
        // Tie-breaker (judgment call per #12): most-recent date wins.
        Assert.Contains("[Date] DESC", SqlComfortBudgetFetcher.WorstCellSql);
        Assert.Contains("[Date] >= @startDate", SqlComfortBudgetFetcher.WorstCellSql);
        Assert.Contains("[Date] <= @endDate", SqlComfortBudgetFetcher.WorstCellSql);
        // Project the surfaced columns.
        Assert.Contains("MeanResidual", SqlComfortBudgetFetcher.WorstCellSql);
        Assert.Contains("MaxAbsZscore", SqlComfortBudgetFetcher.WorstCellSql);
        Assert.Contains("Pattern", SqlComfortBudgetFetcher.WorstCellSql);
    }

    [Fact]
    public void TrendSql_uses_DATE_BUCKET_day_and_aggregates()
    {
        Assert.Contains(
            "dbo.fv_comfortscores_at_cursor(@asOf)",
            SqlComfortBudgetFetcher.TrendSql);
        Assert.Contains("DATE_BUCKET(DAY, 1, BucketTime)", SqlComfortBudgetFetcher.TrendSql);
        Assert.Contains("MIN(Score)", SqlComfortBudgetFetcher.TrendSql);
        Assert.Contains("MAX(Score)", SqlComfortBudgetFetcher.TrendSql);
        Assert.Contains("AVG(", SqlComfortBudgetFetcher.TrendSql);
        Assert.Contains("COUNT(*)", SqlComfortBudgetFetcher.TrendSql);
        Assert.Contains("GROUP BY DATE_BUCKET(DAY, 1, BucketTime)", SqlComfortBudgetFetcher.TrendSql);
        Assert.Contains("ORDER BY Day ASC", SqlComfortBudgetFetcher.TrendSql);
        Assert.Contains("BucketTime >= @start", SqlComfortBudgetFetcher.TrendSql);
        Assert.Contains("BucketTime <= @end", SqlComfortBudgetFetcher.TrendSql);
    }
}
