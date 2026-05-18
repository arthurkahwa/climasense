// SPDX-License-Identifier: MIT
//
// ComfortReadService — slice-7 read facade for the most recent
// `dbo.ComfortScores` row visible at the cursor.
//
// Cursor-aware via `CursorSnapshot`. The cursor-clipping happens at
// the schema level: the SQL goes through the inline TVF
// `dbo.fv_comfortscores_at_cursor(@asOf)` defined in
// `init-db.sql §3.4`, not directly against `dbo.ComfortScores`. This
// matches CONTEXT.md → "CursorSnapshot" which pins the rule: derived
// tables clip via TVFs, not via caller-side `WHERE`.
//
// Interface emergence policy (ADR-0011):
//   This class is concrete; the SQL fetch is parameterised as a
//   `CurrentComfortFetcher` delegate so tests can swap a lambda. No
//   speculative `IComfortReadService` interface — when a second
//   comfort read (e.g. `GET /api/comfort/range`) arrives, the
//   interface is extracted from the two concrete shapes.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;

namespace ClimaSense.Web.Comfort;

/// <summary>
/// Delegate used by <see cref="ComfortReadService"/> to fetch the
/// most recent comfort row. Returns <c>null</c> when no row is
/// visible at the cursor.
/// </summary>
public delegate Task<CurrentComfortDto?> CurrentComfortFetcher(
    DateTime asOf,
    CancellationToken cancellationToken);

public sealed class ComfortReadService
{
    private readonly CurrentComfortFetcher _fetcher;

    public ComfortReadService(CurrentComfortFetcher fetcher)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        _fetcher = fetcher;
    }

    /// <summary>
    /// Return the most recent comfort row visible at the cursor, or
    /// <c>null</c> when no row exists (typically only seen during the
    /// brief lifespan window before the ml-tier comfort scheduler
    /// emits its first row).
    /// </summary>
    public async Task<CurrentComfortDto?> GetCurrentAsync(
        CursorSnapshot cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return await _fetcher(cursor.AsOf, cancellationToken).ConfigureAwait(false);
    }
}
