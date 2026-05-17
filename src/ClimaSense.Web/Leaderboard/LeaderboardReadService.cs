// SPDX-License-Identifier: MIT
//
// LeaderboardReadService — slice-6 read facade for `dbo.Leaderboard`.
//
// The read is NOT cursor-clipped: the leaderboard is a global, append-
// updated table (one row per ModelName), not a per-emission time-series.
// Under `ReplayClock` (slice 12) the cursor doesn't affect what rows
// the Analysis page shows — the metrics describe model behaviour on a
// fixed held-out window, not on the cursor's current position.
//
// Interface emergence policy (ADR-0011):
//   This class is concrete; the SQL fetch is parameterised as a
//   `LeaderboardFetcher` delegate so tests can swap a lambda. No
//   speculative `ILeaderboardService` interface — when a second
//   leaderboard read arrives (e.g. `/api/leaderboard/{provenance}`),
//   the interface is extracted from the two concrete shapes.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClimaSense.Web.Leaderboard;

/// <summary>
/// Delegate used by <see cref="LeaderboardReadService"/> to fetch the
/// full list of leaderboard rows from SQL (one round trip).
/// </summary>
public delegate Task<IReadOnlyList<LeaderboardRowDto>> LeaderboardFetcher(
    CancellationToken cancellationToken);

public sealed class LeaderboardReadService
{
    private readonly LeaderboardFetcher _fetcher;

    public LeaderboardReadService(LeaderboardFetcher fetcher)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        _fetcher = fetcher;
    }

    /// <summary>
    /// Return all `dbo.Leaderboard` rows ordered by MAE ascending.
    /// Empty rows is a valid 200 response (happens during the brief
    /// lifespan window before `LeaderboardSeeder` completes on the
    /// ml tier).
    /// </summary>
    public async Task<LeaderboardResponseDto> GetAllAsync(
        CancellationToken cancellationToken)
    {
        var rows = await _fetcher(cancellationToken).ConfigureAwait(false);
        return new LeaderboardResponseDto(rows);
    }
}
