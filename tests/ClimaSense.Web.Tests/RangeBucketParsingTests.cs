// SPDX-License-Identifier: MIT
//
// Slice-4 wire-spelling tests for `RangeBucket`. Locks the four
// canonical literals (`raw`, `hour`, `day`, `week`) PLUS three
// dashboard-friendly aliases the endpoint accepts so users can type
// `hourly` / `daily` / `weekly` and still hit the right enum case.

#nullable enable

using System;
using ClimaSense.Web.Readings;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class RangeBucketParsingTests
{
    [Theory]
    [InlineData("raw", RangeBucket.Raw)]
    [InlineData("RAW", RangeBucket.Raw)]
    [InlineData("hour", RangeBucket.Hour)]
    [InlineData("Hour", RangeBucket.Hour)]
    [InlineData("HOURLY", RangeBucket.Hour)]
    [InlineData("hourly", RangeBucket.Hour)]
    [InlineData("day", RangeBucket.Day)]
    [InlineData("daily", RangeBucket.Day)]
    [InlineData("week", RangeBucket.Week)]
    [InlineData("Weekly", RangeBucket.Week)]
    public void TryParseWire_accepts_canonical_and_alias_spellings(
        string input,
        RangeBucket expected)
    {
        var ok = RangeBucketExtensions.TryParseWire(input, out var actual);
        Assert.True(ok);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("month")]
    [InlineData("minute")]
    [InlineData("12h")]
    [InlineData("rawish")]
    public void TryParseWire_rejects_unknown_spellings(string? input)
    {
        var ok = RangeBucketExtensions.TryParseWire(input, out var actual);
        Assert.False(ok);
        Assert.Equal(default, actual);
    }

    [Theory]
    [InlineData(RangeBucket.Raw, "raw")]
    [InlineData(RangeBucket.Hour, "hour")]
    [InlineData(RangeBucket.Day, "day")]
    [InlineData(RangeBucket.Week, "week")]
    public void ToWire_emits_canonical_lowercase(RangeBucket bucket, string expected)
    {
        Assert.Equal(expected, bucket.ToWire());
    }

    [Theory]
    [InlineData(RangeBucket.Hour, "HOUR")]
    [InlineData(RangeBucket.Day, "DAY")]
    [InlineData(RangeBucket.Week, "WEEK")]
    public void DateBucketWidth_matches_TSQL_datepart(RangeBucket bucket, string expected)
    {
        Assert.Equal(expected, bucket.DateBucketWidth());
    }

    [Fact]
    public void DateBucketWidth_throws_for_Raw()
    {
        Assert.Throws<InvalidOperationException>(() => RangeBucket.Raw.DateBucketWidth());
    }
}
