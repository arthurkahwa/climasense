// SPDX-License-Identifier: MIT
//
// SensorDataService — slice-3 read facade for `dbo.SensorReadings`.
//
// Cursor-aware via `CursorSnapshot`: every read clips at the cursor's
// `AsOf` so under `ReplayClock` (slice 12) the dashboard reflects the
// cursor's position rather than wall-clock time. Under `WallClock`
// (slice 1's default), the cursor equals UTC now and the query
// behaves like an unclipped "latest row".
//
// Interface emergence policy (ADR-0011):
//   This class is concrete. The single seam that needs testability —
//   the SQL fetch — is parameterised as a `LatestReadingFetcher`
//   delegate injected at construction time. Production wires it to
//   `SqlLatestReadingFetcher.FetchAsync`; tests inject a lambda that
//   records its arguments. No `ISensorDataService` interface; when a
//   second real read-shape arrives (slice 4 ships `/range` and
//   `/heatmap`), the seam is extracted from two concrete classes.
//
// Why Dapper / SqlClient and not EF Core:
//   Slice 4 introduces EF Core. Slice 3's surface is one query — the
//   minimum viable read. `Microsoft.Data.SqlClient` is already pulled
//   in for the readiness probe; using it again for this single
//   `SELECT TOP 1` keeps the slice diff small.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;

namespace ClimaSense.Web.Readings;

/// <summary>
/// Delegate used by <see cref="SensorDataService"/> to fetch the
/// latest reading row. Parameterising the SQL execution lets tests
/// inject a lambda without standing up a SQL Server.
/// </summary>
/// <param name="asOf">The cursor's <see cref="CursorSnapshot.AsOf"/>.</param>
/// <param name="cancellationToken">Bounded per-request cancellation.</param>
public delegate Task<LatestReading?> LatestReadingFetcher(
    DateTime asOf,
    CancellationToken cancellationToken);

/// <summary>
/// Reads the latest <c>SensorReadings</c> row clipped at the cursor.
///
/// Lifetime: registered as a <em>scoped</em> service (it has no
/// cross-request state of its own; the scope just matches the
/// <see cref="CursorSnapshot"/> it consumes).
/// </summary>
public sealed class SensorDataService
{
    private readonly LatestReadingFetcher _fetcher;

    /// <summary>
    /// Construct with the injected fetcher. Throws
    /// <see cref="ArgumentNullException"/> if <paramref name="fetcher"/>
    /// is null — guards against the "registered with no implementation"
    /// startup failure mode.
    /// </summary>
    public SensorDataService(LatestReadingFetcher fetcher)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        _fetcher = fetcher;
    }

    /// <summary>
    /// Return the most recent <c>SensorReadings</c> row at the cursor,
    /// or <c>null</c> when the table is empty (e.g. bootstrap incomplete).
    ///
    /// The SQL executed is:
    /// <code>
    /// SELECT TOP 1 ReadingTime, Temperature, Humidity
    ///   FROM dbo.SensorReadings
    ///  WHERE ReadingTime &lt;= @asOf
    ///  ORDER BY ReadingTime DESC;
    /// </code>
    /// </summary>
    public Task<LatestReading?> GetLatestAsync(
        CursorSnapshot cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return _fetcher(cursor.AsOf, cancellationToken);
    }
}
