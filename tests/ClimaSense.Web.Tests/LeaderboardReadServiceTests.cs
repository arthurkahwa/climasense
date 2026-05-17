// SPDX-License-Identifier: MIT
//
// Slice-6 verification tests for `LeaderboardReadService`. Locks:
//
//   * `GetAllAsync` calls into the injected fetcher exactly once per
//     invocation. There is no cursor (the leaderboard is global —
//     metrics describe model behaviour on a fixed held-out window,
//     not on the cursor's current position).
//   * Empty rows is a 200-shaped response (the page renders the
//     "no rows yet" empty state).
//   * Row ordering is preserved end-to-end — the SQL fetcher already
//     orders by `Mae ASC`; the service must NOT reshuffle.
//   * The pinned SQL targets `dbo.Leaderboard` and orders by
//     `Mae ASC` — guards against a refactor that drops the ORDER BY.
//   * Mape / Smape are nullable on the wire (the sequence_results
//     block in the notebook reports neither).

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Leaderboard;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class LeaderboardReadServiceTests
{
    private sealed class _Recorder
    {
        public int Calls { get; private set; }
        public List<LeaderboardRowDto> Rows { get; set; } = new();
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<LeaderboardRowDto>> Fetch(CancellationToken ct)
        {
            Calls += 1;
            if (Throw is not null)
            {
                throw Throw;
            }
            return Task.FromResult<IReadOnlyList<LeaderboardRowDto>>(Rows);
        }
    }

    [Fact]
    public async Task GetAll_returns_empty_response_when_no_rows()
    {
        var rec = new _Recorder { Rows = new List<LeaderboardRowDto>() };
        var svc = new LeaderboardReadService(rec.Fetch);

        var response = await svc.GetAllAsync(CancellationToken.None);

        Assert.Equal(1, rec.Calls);
        Assert.NotNull(response);
        Assert.Empty(response.Rows);
    }

    [Fact]
    public async Task GetAll_projects_rows_in_fetcher_order()
    {
        var ts = new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc);
        var rec = new _Recorder
        {
            Rows = new List<LeaderboardRowDto>
            {
                new("lag-lr-v1", 0.2144, 0.2933, null, null, "live", ts),
                new("Linear regression (lags)", 0.2144, 0.2933, null, null, "notebook", ts),
                new("Naive (last value)", 0.217, 0.370, 1.164, 1.153, "notebook", ts),
            },
        };
        var svc = new LeaderboardReadService(rec.Fetch);

        var response = await svc.GetAllAsync(CancellationToken.None);

        Assert.Equal(3, response.Rows.Count);
        // Order preserved verbatim — the service does NOT re-sort.
        Assert.Equal("lag-lr-v1", response.Rows[0].ModelName);
        Assert.Equal("Linear regression (lags)", response.Rows[1].ModelName);
        Assert.Equal("Naive (last value)", response.Rows[2].ModelName);
        // Live row carries provenance literal.
        Assert.Equal("live", response.Rows[0].Provenance);
        Assert.Equal("notebook", response.Rows[1].Provenance);
        // Nullable Mape/Smape preserved.
        Assert.Null(response.Rows[0].Mape);
        Assert.Null(response.Rows[0].Smape);
        Assert.Equal(1.164, response.Rows[2].Mape);
        Assert.Equal(1.153, response.Rows[2].Smape);
    }

    [Fact]
    public async Task GetAll_propagates_OperationCanceledException()
    {
        var rec = new _Recorder { Throw = new OperationCanceledException() };
        var svc = new LeaderboardReadService(rec.Fetch);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await svc.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public void SqlLeaderboardFetcher_query_targets_Leaderboard_and_orders_by_mae()
    {
        // Pins the AC: "GET /api/leaderboard returns all rows; the
        // live row carries provenance: 'live', others 'notebook'."
        // Ordering by Mae ASC is the contract — the Analysis page
        // surfaces the best-performing model first.
        Assert.Contains("dbo.Leaderboard", SqlLeaderboardFetcher.Sql);
        Assert.Contains("Mae", SqlLeaderboardFetcher.Sql);
        Assert.Contains("Rmse", SqlLeaderboardFetcher.Sql);
        Assert.Contains("Mape", SqlLeaderboardFetcher.Sql);
        Assert.Contains("Smape", SqlLeaderboardFetcher.Sql);
        Assert.Contains("Provenance", SqlLeaderboardFetcher.Sql);
        Assert.Contains("EvaluatedAt", SqlLeaderboardFetcher.Sql);
        Assert.Contains("ORDER BY Mae ASC", SqlLeaderboardFetcher.Sql);
    }

    [Fact]
    public void Constructor_rejects_null_fetcher()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LeaderboardReadService(null!));
    }
}
