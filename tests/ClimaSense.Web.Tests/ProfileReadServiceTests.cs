// SPDX-License-Identifier: MIT
//
// Slice-9 unit coverage for `ProfileReadService` — defaults, null
// handling, range validation, and pinned SQL shape from
// `SqlProfileFetcher`.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Clock;
using ClimaSense.Web.Cursor;
using ClimaSense.Web.Profiles;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class ProfileReadServiceTests
{
    private static CursorSnapshot MakeCursor(DateTime asOf)
        => new(asOf);

    [Fact]
    public void Constructor_rejects_null_fetcher()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ProfileReadService(null!));
    }

    [Fact]
    public async Task GetRange_null_cursor_throws()
    {
        var svc = new ProfileReadService(
            (asOf, s, e, ct) => Task.FromResult<IReadOnlyList<DayProfileDto>>(
                Array.Empty<DayProfileDto>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            svc.GetRangeAsync(null!, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetRange_default_end_is_cursor_date()
    {
        DateTime asOf = new(2026, 5, 17, 14, 30, 0, DateTimeKind.Utc);
        DateOnly capturedStart = default, capturedEnd = default;
        var svc = new ProfileReadService(
            (a, s, e, ct) =>
            {
                capturedStart = s;
                capturedEnd = e;
                return Task.FromResult<IReadOnlyList<DayProfileDto>>(
                    Array.Empty<DayProfileDto>());
            });

        var resp = await svc.GetRangeAsync(
            MakeCursor(asOf), null, null, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 5, 17), capturedEnd);
        Assert.Equal(new DateOnly(2026, 5, 17).AddDays(-ProfileReadService.DefaultLookbackDays),
            capturedStart);
        Assert.Equal(capturedStart, resp.Start);
        Assert.Equal(capturedEnd, resp.End);
    }

    [Fact]
    public async Task GetRange_explicit_start_and_end_honoured()
    {
        DateOnly capturedStart = default, capturedEnd = default;
        var svc = new ProfileReadService(
            (a, s, e, ct) =>
            {
                capturedStart = s;
                capturedEnd = e;
                return Task.FromResult<IReadOnlyList<DayProfileDto>>(
                    Array.Empty<DayProfileDto>());
            });

        await svc.GetRangeAsync(
            MakeCursor(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc)),
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 1, 1), capturedStart);
        Assert.Equal(new DateOnly(2026, 1, 31), capturedEnd);
    }

    [Fact]
    public async Task GetRange_rejects_start_after_end()
    {
        var svc = new ProfileReadService(
            (a, s, e, ct) => Task.FromResult<IReadOnlyList<DayProfileDto>>(
                Array.Empty<DayProfileDto>()));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GetRangeAsync(
                MakeCursor(DateTime.UtcNow),
                new DateOnly(2026, 5, 17),
                new DateOnly(2026, 5, 16),
                CancellationToken.None));
        Assert.Contains("before", ex.Message);
    }

    [Fact]
    public async Task GetRange_rejects_window_over_cap()
    {
        var svc = new ProfileReadService(
            (a, s, e, ct) => Task.FromResult<IReadOnlyList<DayProfileDto>>(
                Array.Empty<DayProfileDto>()));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GetRangeAsync(
                MakeCursor(DateTime.UtcNow),
                new DateOnly(2020, 1, 1),
                new DateOnly(2026, 1, 1),
                CancellationToken.None));
        Assert.Contains("cap", ex.Message);
    }

    [Fact]
    public void SqlProfileFetcher_range_sql_uses_cursor_tvf()
    {
        // Pinned SQL shape — cursor clipping must go through the TVF.
        Assert.Contains(
            "dbo.fv_dayprofiles_at_cursor(@asOf)",
            SqlProfileFetcher.RangeSql);
        Assert.Contains("ORDER BY [Date] ASC", SqlProfileFetcher.RangeSql);
        Assert.Contains("[Date] >= @startDate", SqlProfileFetcher.RangeSql);
        Assert.Contains("[Date] <= @endDate", SqlProfileFetcher.RangeSql);
    }
}
