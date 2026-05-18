// SPDX-License-Identifier: MIT
//
// ComfortBudgetReadService — slice-10 read facade for the three pure
// SQL aggregations that back the Comfort Budget panel
// (`GET /api/comfort/budget`).
//
// Cursor-aware via `CursorSnapshot`. Cursor-clipping is enforced at
// the schema level (the underlying SQL reads through
// `dbo.fv_comfortscores_at_cursor(@asOf)` +
// `dbo.fv_dayprofiles_at_cursor(@asOf)` — see `SqlComfortBudgetFetcher`).
//
// Configuration:
//
//   * `Threshold`  — discomfort threshold. Reads come in below this
//                    score; default 70.0 (per epic + #12 spec). Read
//                    from `COMFORT_DISCOMFORT_THRESHOLD` in
//                    `Program.cs` at DI construction.
//   * `WindowDays` — width of the budget window. Constant 7
//                    (`COMFORT_BUDGET_WINDOW_DAYS` per epic + #12);
//                    surfaced as a configurable here so a future
//                    "30-day budget" view can reuse the same service
//                    without forking SQL.
//
// Interface emergence policy (ADR-0011):
//   This class is concrete; the read is parameterised as a
//   `ComfortBudgetFetcher` delegate so tests can swap a lambda. No
//   speculative `IComfortBudgetService` interface — when a second
//   budget read arrives (none planned), the interface is extracted
//   from two concrete shapes, not speculated from one.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;

namespace ClimaSense.Web.Comfort;

/// <summary>
/// Delegate used by <see cref="ComfortBudgetReadService"/> to fetch
/// the three aggregations. Returns a fully-populated
/// <see cref="ComfortBudgetDto"/>; empty windows are valid (zero
/// hours, null worstCell, empty trend).
/// </summary>
public delegate Task<ComfortBudgetDto> ComfortBudgetFetcher(
    DateTime asOf,
    DateTime windowStart,
    DateTime windowEnd,
    int windowDays,
    double threshold,
    CancellationToken cancellationToken);

public sealed class ComfortBudgetReadService
{
    private readonly ComfortBudgetFetcher _fetcher;

    /// <summary>
    /// Default discomfort threshold per the epic (#2 → "ML and
    /// analytics") and the #12 spec body. Comfort scores below this
    /// count as "outside the zone."
    /// </summary>
    public const double DefaultThreshold = 70.0;

    /// <summary>
    /// Default budget window in days per the epic
    /// (`COMFORT_BUDGET_WINDOW_DAYS`). 7 days = one calendar week,
    /// matching the dashboard panel title.
    /// </summary>
    public const int DefaultWindowDays = 7;

    /// <summary>The threshold this instance reports against.</summary>
    public double Threshold { get; }

    /// <summary>The window width this instance reports against.</summary>
    public int WindowDays { get; }

    public ComfortBudgetReadService(
        ComfortBudgetFetcher fetcher,
        double threshold = DefaultThreshold,
        int windowDays = DefaultWindowDays)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        if (threshold < 0.0 || threshold > 100.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                threshold,
                "Threshold must be in [0, 100] (comfort scores are 0–100).");
        }
        if (windowDays <= 0 || windowDays > 366)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowDays),
                windowDays,
                "WindowDays must be in (0, 366].");
        }
        _fetcher = fetcher;
        Threshold = threshold;
        WindowDays = windowDays;
    }

    /// <summary>
    /// Compute the budget at the cursor's current position. The window
    /// is <c>[cursor - WindowDays, cursor]</c>; both endpoints are
    /// inclusive on the SQL side because <c>BucketTime</c> is
    /// hour-aligned and the cursor lands at a finer resolution.
    /// </summary>
    public async Task<ComfortBudgetDto> GetAsync(
        CursorSnapshot cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var asOf = cursor.AsOf;
        var windowEnd = asOf;
        var windowStart = asOf.AddDays(-WindowDays);

        return await _fetcher(
            asOf,
            windowStart,
            windowEnd,
            WindowDays,
            Threshold,
            cancellationToken).ConfigureAwait(false);
    }
}
