// SPDX-License-Identifier: MIT
//
// RangeBucket — slice-4 enum mirroring the `RangeBucket` schema in
// `contracts/openapi.yaml`.
//
// Why a hand-written enum instead of the Kiota-generated
// `Models.RangeBucket`:
//
//   * Kiota's generated enum is on the *client* side (the .NET tier
//     calling into ml). The slice-4 endpoints are served BY the .NET
//     tier; the parser used by `Program.cs` reads the wire literal
//     out of the query string. Re-using the Kiota enum here would
//     entangle a client-side type with a server-side responsibility.
//   * The wire literals (`raw`, `hour`, `day`, `week`) are pinned by
//     the contract; the local enum's `TryParseWire` keeps the
//     case-insensitive parsing in one place. The `RangeBucketParsingTests`
//     suite locks both spellings and rejected literals.

#nullable enable

using System;

namespace ClimaSense.Web.Readings;

/// <summary>
/// Aggregation granularity for <c>/api/readings/range</c>. Wire names
/// (<c>raw</c>, <c>hour</c>, <c>day</c>, <c>week</c>) match the
/// <c>RangeBucket</c> schema in <c>contracts/openapi.yaml</c>.
/// </summary>
public enum RangeBucket
{
    /// <summary>Un-aggregated rows (capped by <c>CLIMASENSE_RAW_MAX_DAYS</c>).</summary>
    Raw = 0,

    /// <summary><c>DATE_BUCKET(HOUR, 1, ReadingTime)</c>.</summary>
    Hour = 1,

    /// <summary><c>DATE_BUCKET(DAY, 1, ReadingTime)</c>.</summary>
    Day = 2,

    /// <summary><c>DATE_BUCKET(WEEK, 1, ReadingTime)</c>.</summary>
    Week = 3,
}

/// <summary>
/// Parsing + spelling helpers for <see cref="RangeBucket"/>. Used by
/// the endpoint handler in <c>Program.cs</c> and by tests.
/// </summary>
public static class RangeBucketExtensions
{
    /// <summary>The wire-canonical lowercase spelling.</summary>
    public static string ToWire(this RangeBucket bucket) => bucket switch
    {
        RangeBucket.Raw => "raw",
        RangeBucket.Hour => "hour",
        RangeBucket.Day => "day",
        RangeBucket.Week => "week",
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null),
    };

    /// <summary>
    /// Case-insensitive parse from the wire literal. Returns <c>true</c>
    /// on a successful parse; <c>false</c> when the input is null/empty
    /// or doesn't match one of the four canonical spellings.
    /// </summary>
    public static bool TryParseWire(string? value, out RangeBucket bucket)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            bucket = default;
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "raw":
                bucket = RangeBucket.Raw;
                return true;
            case "hour":
            case "hourly":
                bucket = RangeBucket.Hour;
                return true;
            case "day":
            case "daily":
                bucket = RangeBucket.Day;
                return true;
            case "week":
            case "weekly":
                bucket = RangeBucket.Week;
                return true;
            default:
                bucket = default;
                return false;
        }
    }

    /// <summary>
    /// The SQL Server <c>DATE_BUCKET</c> width literal (e.g. <c>HOUR</c>)
    /// for the bucket. Throws for <c>Raw</c> — callers must branch on
    /// raw vs aggregated before constructing SQL.
    /// </summary>
    public static string DateBucketWidth(this RangeBucket bucket) => bucket switch
    {
        RangeBucket.Hour => "HOUR",
        RangeBucket.Day => "DAY",
        RangeBucket.Week => "WEEK",
        RangeBucket.Raw => throw new InvalidOperationException(
            "RangeBucket.Raw has no DATE_BUCKET width — the raw path skips bucketing entirely."),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null),
    };
}
