// SPDX-License-Identifier: MIT
//
// AnomalyReadService — slice-8 read facade for `dbo.Anomalies` visible
// at the cursor.
//
// Cursor-aware via `CursorSnapshot`. Cursor-clipping happens at the
// schema level: the SQL goes through the inline TVF
// `dbo.fv_anomalies_at_cursor(@asOf)` defined in `init-db.sql §3.2`,
// not directly against `dbo.Anomalies`. This matches CONTEXT.md →
// "CursorSnapshot" which pins the rule: derived tables clip via TVFs,
// not via caller-side `WHERE`.
//
// Interface emergence policy (ADR-0011):
//   This class is concrete; the two SQL fetches are parameterised as
//   `LatestAnomalyFetcher` + `AnomalyRangeFetcher` delegates so tests
//   can swap a lambda. No speculative `IAnomalyReadService` interface
//   — when a second anomaly read arrives (e.g. an SSE-driven push of
//   the very-latest anomaly), the interface is extracted from the
//   two concrete shapes.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;

namespace ClimaSense.Web.Anomalies;

/// <summary>
/// Delegate used by <see cref="AnomalyReadService"/> to fetch the
/// most recent anomaly row. Returns <c>null</c> when no row is
/// visible at the cursor.
/// </summary>
public delegate Task<LatestAnomalyDto?> LatestAnomalyFetcher(
    DateTime asOf,
    CancellationToken cancellationToken);

/// <summary>
/// Delegate used by <see cref="AnomalyReadService"/> to fetch the
/// anomalies in a range. <paramref name="anomalyType"/> filters to
/// a single <c>AnomalyType</c> when non-null.
/// </summary>
public delegate Task<IReadOnlyList<LatestAnomalyDto>> AnomalyRangeFetcher(
    DateTime asOf,
    DateTime start,
    DateTime end,
    string? anomalyType,
    CancellationToken cancellationToken);

public sealed class AnomalyReadService
{
    private readonly LatestAnomalyFetcher _latestFetcher;
    private readonly AnomalyRangeFetcher _rangeFetcher;

    public AnomalyReadService(
        LatestAnomalyFetcher latestFetcher,
        AnomalyRangeFetcher rangeFetcher)
    {
        ArgumentNullException.ThrowIfNull(latestFetcher);
        ArgumentNullException.ThrowIfNull(rangeFetcher);
        _latestFetcher = latestFetcher;
        _rangeFetcher = rangeFetcher;
    }

    /// <summary>
    /// Default lookback for <see cref="GetRangeAsync"/> when the caller
    /// omits <c>start</c>: 24 hours back from <c>end</c>.
    /// </summary>
    public static readonly TimeSpan DefaultLookback = TimeSpan.FromHours(24);

    /// <summary>
    /// Server-side cap on the requested window to prevent unbounded
    /// scans. Matches the changepoint detector's scan window so the
    /// upper bound is the same one used by the writer.
    /// </summary>
    public static readonly TimeSpan MaxLookback = TimeSpan.FromDays(90);

    /// <summary>
    /// Return the most recent anomaly row visible at the cursor, or
    /// <c>null</c> when no row exists (typically only seen during the
    /// brief lifespan window before the ml-tier scheduler emits its
    /// first detector tick).
    /// </summary>
    public async Task<LatestAnomalyDto?> GetLatestAsync(
        CursorSnapshot cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return await _latestFetcher(cursor.AsOf, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Return anomalies in [<paramref name="start"/>, <paramref name="end"/>]
    /// visible at the cursor, optionally filtered to a single
    /// <paramref name="anomalyType"/>. Defaults: <paramref name="end"/> →
    /// the cursor's <c>AsOf</c>; <paramref name="start"/> →
    /// <paramref name="end"/> − 24 h.
    /// </summary>
    public async Task<AnomaliesResponseDto> GetRangeAsync(
        CursorSnapshot cursor,
        DateTime? start,
        DateTime? end,
        string? anomalyType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var resolvedEnd = end ?? cursor.AsOf;
        var resolvedStart = start ?? (resolvedEnd - DefaultLookback);

        if (resolvedStart > resolvedEnd)
        {
            throw new ArgumentException(
                "`start` must be on or before `end`.",
                nameof(start));
        }
        if (resolvedEnd - resolvedStart > MaxLookback)
        {
            throw new ArgumentException(
                $"Window exceeds the {MaxLookback.TotalDays:F0}-day cap.",
                nameof(start));
        }

        var rows = await _rangeFetcher(
            cursor.AsOf, resolvedStart, resolvedEnd, anomalyType, cancellationToken)
            .ConfigureAwait(false);
        return new AnomaliesResponseDto(
            Start: resolvedStart,
            End: resolvedEnd,
            Type: anomalyType,
            Rows: rows);
    }
}
