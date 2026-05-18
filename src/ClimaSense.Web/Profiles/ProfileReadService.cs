// SPDX-License-Identifier: MIT
//
// ProfileReadService — slice-9 read facade for `dbo.DayProfiles`
// visible at the cursor.
//
// Cursor-aware via `CursorSnapshot`. Cursor-clipping happens at the
// schema level: the SQL goes through the inline TVF
// `dbo.fv_dayprofiles_at_cursor(@asOf)` defined in `init-db.sql §3.3`,
// not directly against `dbo.DayProfiles`.
//
// Interface emergence policy (ADR-0011):
//   This class is concrete; the read is parameterised as a
//   `DayProfileRangeFetcher` delegate so tests can swap a lambda. No
//   speculative `IProfileReadService` interface — when a second
//   profile read arrives, the interface is extracted from the
//   concrete shape(s).

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;

namespace ClimaSense.Web.Profiles;

/// <summary>
/// Delegate used by <see cref="ProfileReadService"/> to fetch
/// <c>dbo.DayProfiles</c> rows in <c>[start, end]</c> visible at
/// the cursor.
/// </summary>
public delegate Task<IReadOnlyList<DayProfileDto>> DayProfileRangeFetcher(
    DateTime asOf,
    DateOnly start,
    DateOnly end,
    CancellationToken cancellationToken);

public sealed class ProfileReadService
{
    private readonly DayProfileRangeFetcher _rangeFetcher;

    public ProfileReadService(DayProfileRangeFetcher rangeFetcher)
    {
        ArgumentNullException.ThrowIfNull(rangeFetcher);
        _rangeFetcher = rangeFetcher;
    }

    /// <summary>
    /// Default lookback when the caller omits <c>start</c>: 30 days
    /// back from <c>end</c>. Matches the typical "month-at-a-glance"
    /// view the dashboard expects.
    /// </summary>
    public static readonly int DefaultLookbackDays = 30;

    /// <summary>
    /// Server-side cap on the requested window. 366 days = one
    /// calendar year — wider requests are almost certainly a bug
    /// in the caller, not a real query.
    /// </summary>
    public static readonly int MaxLookbackDays = 366;

    /// <summary>
    /// Return profile rows in <c>[start, end]</c> visible at the
    /// cursor. Defaults: <c>end</c> → the cursor's calendar date;
    /// <c>start</c> → <c>end</c> − <see cref="DefaultLookbackDays"/>.
    /// </summary>
    public async Task<DayProfilesResponseDto> GetRangeAsync(
        CursorSnapshot cursor,
        DateOnly? start,
        DateOnly? end,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var resolvedEnd = end ?? DateOnly.FromDateTime(cursor.AsOf);
        var resolvedStart = start
            ?? resolvedEnd.AddDays(-DefaultLookbackDays);

        if (resolvedStart > resolvedEnd)
        {
            throw new ArgumentException(
                "`start` must be on or before `end`.",
                nameof(start));
        }
        var span = resolvedEnd.DayNumber - resolvedStart.DayNumber + 1;
        if (span > MaxLookbackDays)
        {
            throw new ArgumentException(
                $"Window exceeds the {MaxLookbackDays}-day cap.",
                nameof(start));
        }

        var rows = await _rangeFetcher(
            cursor.AsOf,
            resolvedStart,
            resolvedEnd,
            cancellationToken).ConfigureAwait(false);
        return new DayProfilesResponseDto(
            Start: resolvedStart,
            End: resolvedEnd,
            Rows: rows);
    }
}
