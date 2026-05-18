// SPDX-License-Identifier: MIT
//
// AlertReadService — slice-11 read facade for `dbo.Alerts` visible at
// the cursor.
//
// Cursor-aware via `CursorSnapshot`. Cursor-clipping happens at the
// schema level via the inline TVF `dbo.fv_alerts_at_cursor(@asOf)`
// (init-db.sql §3.5 — clips on `ReplayClockAtFire`).
//
// Interface emergence policy (ADR-0011): concrete; the read is
// parameterised as a `AlertHistoryFetcher` delegate so tests can swap
// a lambda.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;

namespace ClimaSense.Web.Alerts;

/// <summary>
/// Delegate used by <see cref="AlertReadService"/> to fetch up to
/// <c>limit</c> most-recent <c>dbo.Alerts</c> rows visible at the
/// cursor. Joined against <c>dbo.AlertRules</c> for the human-readable
/// rule name.
/// </summary>
public delegate Task<IReadOnlyList<AlertRowDto>> AlertHistoryFetcher(
    DateTime asOf,
    int limit,
    CancellationToken cancellationToken);

public sealed class AlertReadService
{
    /// <summary>Default <c>limit</c> when caller omits the query string.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Server-side ceiling on <c>limit</c>.</summary>
    public const int MaxLimit = 200;

    private readonly AlertHistoryFetcher _historyFetcher;

    public AlertReadService(AlertHistoryFetcher historyFetcher)
    {
        ArgumentNullException.ThrowIfNull(historyFetcher);
        _historyFetcher = historyFetcher;
    }

    /// <summary>
    /// Return up to <paramref name="limit"/> most-recent alerts
    /// visible at the cursor. <paramref name="limit"/> is clamped to
    /// <c>[1, <see cref="MaxLimit"/>]</c>; <c>null</c> uses
    /// <see cref="DefaultLimit"/>.
    /// </summary>
    public async Task<AlertHistoryResponseDto> GetHistoryAsync(
        CursorSnapshot cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var resolved = limit ?? DefaultLimit;
        if (resolved < 1) resolved = 1;
        if (resolved > MaxLimit) resolved = MaxLimit;

        var rows = await _historyFetcher(cursor.AsOf, resolved, cancellationToken)
            .ConfigureAwait(false);
        return new AlertHistoryResponseDto(
            Limit: resolved,
            Count: rows.Count,
            Rows: rows);
    }
}
