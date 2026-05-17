// SPDX-License-Identifier: MIT
//
// ForecastReadService — slice-5 read facade for the most recent
// `Forecasts` batch visible at the cursor.
//
// Cursor-aware via `CursorSnapshot`. The cursor-clipping happens at
// the schema level: the SQL goes through the inline TVF
// `dbo.fv_forecasts_at_cursor(@asOf)` defined in `init-db.sql §3.1`,
// not directly against `dbo.Forecasts`. This matches CONTEXT.md →
// "CursorSnapshot" which pins the rule: derived tables clip via TVFs,
// not via caller-side `WHERE`.
//
// Interface emergence policy (ADR-0011):
//   This class is concrete; the SQL fetch is parameterised as a
//   `LatestForecastFetcher` delegate so tests can swap a lambda. No
//   speculative `IForecastReadService` interface — when a second
//   forecast read (e.g. `GET /api/forecasts/range`) arrives, the
//   interface is extracted from the two concrete shapes.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;

namespace ClimaSense.Web.Forecasts;

/// <summary>
/// Delegate used by <see cref="ForecastReadService"/> to fetch the
/// most recent forecast batch.
/// </summary>
public delegate Task<IReadOnlyList<ForecastPointDto>> LatestForecastFetcher(
    DateTime asOf,
    CancellationToken cancellationToken);

/// <summary>
/// Delegate companion that also surfaces the batch's `GeneratedAt` and
/// `ModelVersion` (one query, two outputs — kept as a single delegate
/// so the production adapter executes one SQL statement).
/// </summary>
public delegate Task<(DateTime? GeneratedAt, string? ModelVersion, IReadOnlyList<ForecastPointDto> Points)>
    LatestForecastBatchFetcher(DateTime asOf, CancellationToken cancellationToken);

public sealed class ForecastReadService
{
    private readonly LatestForecastBatchFetcher _fetcher;

    public ForecastReadService(LatestForecastBatchFetcher fetcher)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        _fetcher = fetcher;
    }

    /// <summary>
    /// Return the most recent forecast batch visible at the cursor.
    /// When no forecast has been emitted at or before `cursor.AsOf`,
    /// returns an envelope with `Points.Count == 0` and `HorizonHours == 0`
    /// — a valid 200 response (the dashboard can render "no forecast yet").
    /// </summary>
    public async Task<ForecastEnvelopeDto> GetLatestAsync(
        CursorSnapshot cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var (generatedAt, modelVersion, points) = await _fetcher(cursor.AsOf, cancellationToken)
            .ConfigureAwait(false);

        if (points.Count == 0 || generatedAt is null)
        {
            return new ForecastEnvelopeDto(
                GeneratedAt: cursor.AsOf,
                ModelVersion: modelVersion ?? "lag-lr-v1",
                HorizonHours: 0,
                Points: Array.Empty<ForecastPointDto>());
        }

        return new ForecastEnvelopeDto(
            GeneratedAt: generatedAt.Value,
            ModelVersion: modelVersion ?? "lag-lr-v1",
            HorizonHours: points.Count,
            Points: points);
    }
}
