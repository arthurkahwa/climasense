using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class TimeTests
{
    [Fact]
    public void Cet_summer_reading_converts_to_utc_minus_two()
    {
        var utc = CetZone.ToUtc(new DateTime(2026, 6, 15, 18, 0, 0));
        Assert.Equal(new DateTime(2026, 6, 15, 16, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void IsStale_true_when_older_than_threshold()
    {
        var readingCet = new DateTime(2026, 6, 15, 18, 0, 0);
        var nowUtc = new DateTime(2026, 6, 15, 16, 45, 0, DateTimeKind.Utc);
        Assert.True(Freshness.IsStale(readingCet, nowUtc, 30));
        Assert.Equal(45, Freshness.MinutesOld(readingCet, nowUtc));
    }

    [Fact]
    public void IsStale_false_when_fresh()
    {
        var readingCet = new DateTime(2026, 6, 15, 18, 0, 0);
        var nowUtc = new DateTime(2026, 6, 15, 16, 10, 0, DateTimeKind.Utc);
        Assert.False(Freshness.IsStale(readingCet, nowUtc, 30));
    }

    [Fact]
    public void ToUtc_handles_spring_forward_gap_without_throwing()
    {
        // 2026-03-29 02:30 CET does not exist (spring forward); must not throw.
        // Nudged to 03:30 CEST (UTC+2) => 01:30 UTC.
        var utc = CetZone.ToUtc(new DateTime(2026, 3, 29, 2, 30, 0));
        Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), utc);
    }
}
