// SPDX-License-Identifier: MIT
//
// Slice-7 verification tests for `ComfortReadService`. Locks:
//
//   * `GetCurrentAsync` calls the injected fetcher exactly once per
//     invocation, passing `cursor.AsOf`.
//   * A `null` fetcher result becomes a `null` service result (the
//     endpoint translates that into 404).
//   * A non-null fetcher result round-trips verbatim through the
//     service.
//   * The pinned SQL targets the `dbo.fv_comfortscores_at_cursor`
//     TVF and orders by `BucketTime DESC TOP 1` — guards against a
//     refactor that drops the cursor-clip or the ordering.
//   * Constructor rejects a null fetcher.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Comfort;
using ClimaSense.Web.Cursor;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class ComfortReadServiceTests
{
    private sealed class _Recorder
    {
        public int Calls { get; private set; }
        public DateTime LastAsOf { get; private set; }
        public CurrentComfortDto? Returns { get; set; }
        public Exception? Throw { get; set; }

        public Task<CurrentComfortDto?> Fetch(DateTime asOf, CancellationToken ct)
        {
            Calls += 1;
            LastAsOf = asOf;
            if (Throw is not null)
            {
                throw Throw;
            }
            return Task.FromResult(Returns);
        }
    }

    private static CursorSnapshot _CursorAt(DateTime ts) =>
        new CursorSnapshot(DateTime.SpecifyKind(ts, DateTimeKind.Utc));

    [Fact]
    public async Task GetCurrent_returns_null_when_table_empty()
    {
        var rec = new _Recorder { Returns = null };
        var svc = new ComfortReadService(rec.Fetch);
        var cursor = _CursorAt(new DateTime(2026, 5, 17, 12, 0, 0));

        var result = await svc.GetCurrentAsync(cursor, CancellationToken.None);

        Assert.Equal(1, rec.Calls);
        Assert.Null(result);
        Assert.Equal(cursor.AsOf, rec.LastAsOf);
    }

    [Fact]
    public async Task GetCurrent_returns_row_verbatim_when_present()
    {
        var bucket = new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc);
        var computedAt = new DateTime(2026, 5, 17, 11, 0, 5, DateTimeKind.Utc);
        var row = new CurrentComfortDto(
            Score: 78.50,
            Rating: "acceptable",
            Season: "summer",
            BucketTime: bucket,
            ComputedAt: computedAt);
        var rec = new _Recorder { Returns = row };
        var svc = new ComfortReadService(rec.Fetch);
        var cursor = _CursorAt(new DateTime(2026, 5, 17, 11, 30, 0));

        var result = await svc.GetCurrentAsync(cursor, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(78.50, result!.Score);
        Assert.Equal("acceptable", result.Rating);
        Assert.Equal("summer", result.Season);
        Assert.Equal(bucket, result.BucketTime);
        Assert.Equal(computedAt, result.ComputedAt);
    }

    [Fact]
    public async Task GetCurrent_propagates_OperationCanceledException()
    {
        var rec = new _Recorder { Throw = new OperationCanceledException() };
        var svc = new ComfortReadService(rec.Fetch);
        var cursor = _CursorAt(DateTime.UtcNow);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await svc.GetCurrentAsync(cursor, CancellationToken.None));
    }

    [Fact]
    public async Task GetCurrent_rejects_null_cursor()
    {
        var svc = new ComfortReadService(
            (asOf, ct) => Task.FromResult<CurrentComfortDto?>(null));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await svc.GetCurrentAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void Constructor_rejects_null_fetcher()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ComfortReadService(null!));
    }

    [Fact]
    public void SqlComfortFetcher_query_targets_cursor_tvf_and_orders_by_bucket()
    {
        // Pins the AC: "Dashboard's comfort card shows score, rating
        // label, and season label; reloading shows the same values."
        // The latest row at the cursor must be the source — TOP 1
        // ORDER BY BucketTime DESC against the cursor-clipped TVF.
        Assert.Contains("fv_comfortscores_at_cursor", SqlComfortFetcher.Sql);
        Assert.Contains("@asOf", SqlComfortFetcher.Sql);
        Assert.Contains("BucketTime", SqlComfortFetcher.Sql);
        Assert.Contains("Score", SqlComfortFetcher.Sql);
        Assert.Contains("Rating", SqlComfortFetcher.Sql);
        Assert.Contains("Season", SqlComfortFetcher.Sql);
        Assert.Contains("ComputedAt", SqlComfortFetcher.Sql);
        Assert.Contains("TOP 1", SqlComfortFetcher.Sql);
        Assert.Contains("ORDER BY BucketTime DESC", SqlComfortFetcher.Sql);
    }
}
