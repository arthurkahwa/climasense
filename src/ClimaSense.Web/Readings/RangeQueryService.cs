// SPDX-License-Identifier: MIT
//
// RangeQueryService — slice-4 read facade for the historical
// Explorer's two endpoints:
//
//   * GET /api/readings/range    — DATE_BUCKET aggregation or raw rows.
//   * GET /api/readings/heatmap  — per-day mean temperature for a year.
//
// Cursor-aware: both reads receive a `CursorSnapshot` and clip with
// `WHERE ReadingTime <= @asOf` so under `ReplayClock` (slice 12) the
// Explorer respects the cursor.
//
// Interface emergence policy (ADR-0011 / CONTEXT.md):
//   Concrete class with two delegate seams (`RangeFetcher` +
//   `HeatmapFetcher`). The seams are parameterised because the only
//   second adapter we have is the per-test in-memory fake — extracting
//   a full `IRangeRepository` would be speculative. Slice 3's
//   `SensorDataService` uses the same pattern with one delegate; we now
//   have two concrete read-shapes living next to it. If a third
//   reading-side read appears (e.g. live forecast read), the
//   interface might cohere — but we wait for the third to inform the
//   shape.
//
// Bounded `raw` window:
//   Raw requests skip aggregation and stream individual rows. To keep
//   responses bounded, the service rejects raw windows wider than
//   `CLIMASENSE_RAW_MAX_DAYS` (default 7). The cap is applied at the
//   *service* boundary so SQL never sees a runaway scan request.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;

namespace ClimaSense.Web.Readings;

/// <summary>
/// Fetches one bucketed range from <c>SensorReadings</c>.
/// Implementations clip on the cursor's <paramref name="asOf"/>.
/// </summary>
/// <param name="bucket">Granularity selected by the caller.</param>
/// <param name="start">Inclusive lower bound, post cursor-clip.</param>
/// <param name="end">Inclusive upper bound, post cursor-clip.</param>
/// <param name="asOf">Cursor cap for the underlying scan.</param>
/// <param name="cancellationToken">Bounded per-request cancellation.</param>
public delegate Task<IReadOnlyList<BucketedReading>> RangeFetcher(
    RangeBucket bucket,
    DateTime start,
    DateTime end,
    DateTime asOf,
    CancellationToken cancellationToken);

/// <summary>
/// Fetches every populated daily-mean cell for a year. Empty days are
/// NOT returned by the fetcher; the service materialises a fully-dense
/// 365/366-cell calendar by left-joining the fetcher's results onto a
/// generated date sequence.
/// </summary>
/// <param name="yearStart">First instant of the requested year (UTC).</param>
/// <param name="yearEnd">Exclusive upper bound — first instant of the next year (UTC).</param>
/// <param name="asOf">Cursor cap.</param>
/// <param name="cancellationToken">Bounded per-request cancellation.</param>
public delegate Task<IReadOnlyList<HeatmapCell>> HeatmapFetcher(
    DateTime yearStart,
    DateTime yearEnd,
    DateTime asOf,
    CancellationToken cancellationToken);

/// <summary>
/// Bounded inputs for <see cref="RangeQueryService.GetRangeAsync"/>.
/// </summary>
public sealed record RangeQueryArgs(
    DateTime Start,
    DateTime End,
    RangeBucket Bucket);

/// <summary>
/// Validation outcomes for <see cref="RangeQueryService.GetRangeAsync"/>.
/// Exposed publicly so the endpoint handler can map each variant to the
/// right HTTP status / ProblemDetails body.
/// </summary>
public enum RangeQueryError
{
    None = 0,
    StartAfterEnd = 1,
    RawWindowTooLarge = 2,
}

/// <summary>
/// Reads bucketed ranges and heatmap cells from the sensor stream.
///
/// Lifetime: scoped (matches <see cref="CursorSnapshot"/>'s scope).
/// </summary>
public sealed class RangeQueryService
{
    /// <summary>Default cap on a `raw` window. Overridable via env var.</summary>
    public const int DefaultRawMaxDays = 7;

    private readonly RangeFetcher _rangeFetcher;
    private readonly HeatmapFetcher _heatmapFetcher;
    private readonly int _rawMaxDays;

    public RangeQueryService(
        RangeFetcher rangeFetcher,
        HeatmapFetcher heatmapFetcher,
        int rawMaxDays = DefaultRawMaxDays)
    {
        ArgumentNullException.ThrowIfNull(rangeFetcher);
        ArgumentNullException.ThrowIfNull(heatmapFetcher);
        if (rawMaxDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rawMaxDays),
                rawMaxDays,
                "Raw window cap must be positive.");
        }

        _rangeFetcher = rangeFetcher;
        _heatmapFetcher = heatmapFetcher;
        _rawMaxDays = rawMaxDays;
    }

    /// <summary>The configured cap on `raw` window width in days.</summary>
    public int RawMaxDays => _rawMaxDays;

    /// <summary>
    /// Validate inputs (without touching SQL) and return either an error
    /// code or the cursor-clipped <c>(start, end)</c> the fetcher will
    /// be invoked with.
    /// </summary>
    public RangeQueryError ValidateAndClip(
        CursorSnapshot cursor,
        RangeQueryArgs args,
        out DateTime clippedStart,
        out DateTime clippedEnd)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(args);

        var start = NormaliseUtc(args.Start);
        var end = NormaliseUtc(args.End);

        // The cursor caps the *end* of the window. A start past the
        // cursor produces an empty window — that is allowed and returns
        // an empty bucket array; it is NOT a 400.
        if (end > cursor.AsOf)
        {
            end = cursor.AsOf;
        }

        clippedStart = start;
        clippedEnd = end;

        if (start > end)
        {
            return RangeQueryError.StartAfterEnd;
        }

        if (args.Bucket == RangeBucket.Raw
            && (end - start).TotalDays > _rawMaxDays)
        {
            return RangeQueryError.RawWindowTooLarge;
        }

        return RangeQueryError.None;
    }

    /// <summary>
    /// Execute a validated range query. Throws if validation has not
    /// been performed via <see cref="ValidateAndClip"/> first (callers
    /// should branch on the outcome there).
    /// </summary>
    public async Task<BucketedReadingsResponse> GetRangeAsync(
        CursorSnapshot cursor,
        RangeQueryArgs args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(args);

        var error = ValidateAndClip(cursor, args, out var start, out var end);
        if (error != RangeQueryError.None)
        {
            throw new InvalidOperationException(
                $"GetRangeAsync called with invalid args ({error}). " +
                "Callers must call ValidateAndClip first.");
        }

        var rows = await _rangeFetcher(
                args.Bucket, start, end, cursor.AsOf, cancellationToken)
            .ConfigureAwait(false);

        // For aggregated buckets, fill empty buckets so the dashboard
        // sees a uniform grid. Raw skips this — every fetched row
        // already represents itself.
        IReadOnlyList<BucketedReading> dense = args.Bucket switch
        {
            RangeBucket.Raw => rows,
            _ => DensifyBuckets(rows, start, end, args.Bucket),
        };

        return new BucketedReadingsResponse(
            Start: start,
            End: end,
            Bucket: args.Bucket.ToWire(),
            Buckets: dense);
    }

    /// <summary>
    /// Execute the heatmap fetch for <paramref name="year"/>, then
    /// produce a fully-dense 365/366-cell calendar. Days with no data
    /// land in the response with <c>sampleCount: 0</c> and
    /// <c>temperatureMean: null</c>.
    /// </summary>
    public async Task<HeatmapResponse> GetHeatmapAsync(
        CursorSnapshot cursor,
        int year,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        if (year is < 1900 or > 2100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(year),
                year,
                "Year must be in [1900, 2100].");
        }

        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var fetched = await _heatmapFetcher(
                yearStart, yearEnd, cursor.AsOf, cancellationToken)
            .ConfigureAwait(false);

        // Build a (date -> cell) map then walk the year emitting either
        // the fetched value or an empty cell.
        var byDate = new Dictionary<DateOnly, HeatmapCell>(capacity: fetched.Count);
        foreach (var cell in fetched)
        {
            byDate[cell.Date] = cell;
        }

        var totalDays = DateTime.IsLeapYear(year) ? 366 : 365;
        var cells = new List<HeatmapCell>(capacity: totalDays);
        var anchor = new DateOnly(year, 1, 1);
        for (var i = 0; i < totalDays; i++)
        {
            var d = anchor.AddDays(i);
            if (byDate.TryGetValue(d, out var fetchedCell))
            {
                cells.Add(fetchedCell);
            }
            else
            {
                cells.Add(new HeatmapCell(d, SampleCount: 0, TemperatureMean: null));
            }
        }

        return new HeatmapResponse(Year: year, Cells: cells);
    }

    // -----------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------
    private static DateTime NormaliseUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    /// <summary>
    /// Walk the bucket grid from <paramref name="start"/> to
    /// <paramref name="end"/> at the bucket's stride, emitting either
    /// the fetched bucket (if it matches the bucket-start) or an empty
    /// placeholder. Buckets are aligned on the bucket-width boundaries
    /// (matching SQL Server's <c>DATE_BUCKET</c> output).
    /// </summary>
    internal static IReadOnlyList<BucketedReading> DensifyBuckets(
        IReadOnlyList<BucketedReading> rows,
        DateTime start,
        DateTime end,
        RangeBucket bucket)
    {
        if (rows.Count == 0 && start > end)
        {
            return Array.Empty<BucketedReading>();
        }

        var alignedStart = AlignDown(start, bucket);
        var byTime = new Dictionary<DateTime, BucketedReading>(capacity: rows.Count);
        foreach (var r in rows)
        {
            byTime[r.BucketTime] = r;
        }

        var dense = new List<BucketedReading>(capacity: Math.Max(rows.Count, 1));
        var cursor = alignedStart;
        // Half-open interval [start, end): a 168-hour range produces 168 hourly buckets.
        while (cursor < end)
        {
            if (byTime.TryGetValue(cursor, out var existing))
            {
                dense.Add(existing);
            }
            else
            {
                dense.Add(new BucketedReading(
                    BucketTime: cursor,
                    SampleCount: 0,
                    TemperatureMean: null,
                    TemperatureMin: null,
                    TemperatureMax: null,
                    HumidityMean: null,
                    HumidityMin: null,
                    HumidityMax: null));
            }
            cursor = Advance(cursor, bucket);
        }

        return dense;
    }

    /// <summary>
    /// Round <paramref name="value"/> down to the bucket's boundary.
    /// Matches SQL Server's <c>DATE_BUCKET(..., 1, value, '1900-01-01')</c>
    /// semantics for the supported widths (HOUR / DAY / WEEK).
    ///
    /// For WEEK, SQL Server's default origin is <c>1900-01-01</c> which
    /// is a Monday — so WEEK boundaries fall on Mondays. We replicate
    /// that here to match the SQL Server output 1:1.
    /// </summary>
    public static DateTime AlignDown(DateTime value, RangeBucket bucket)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return bucket switch
        {
            RangeBucket.Hour => new DateTime(
                utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc),
            RangeBucket.Day => new DateTime(
                utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
            RangeBucket.Week => AlignToMonday(utc),
            RangeBucket.Raw => utc,
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null),
        };
    }

    private static DateTime AlignToMonday(DateTime utc)
    {
        // .NET's DayOfWeek treats Sunday as 0; we need offset from Monday.
        var dow = (int)utc.DayOfWeek;
        var monOffset = (dow + 6) % 7;  // Mon→0, Tue→1, ..., Sun→6
        var monday = utc.Date.AddDays(-monOffset);
        return DateTime.SpecifyKind(monday, DateTimeKind.Utc);
    }

    internal static DateTime Advance(DateTime value, RangeBucket bucket) => bucket switch
    {
        RangeBucket.Hour => value.AddHours(1),
        RangeBucket.Day => value.AddDays(1),
        RangeBucket.Week => value.AddDays(7),
        RangeBucket.Raw => throw new InvalidOperationException(
            "Raw bucket has no stride — densification is bypassed."),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null),
    };
}
