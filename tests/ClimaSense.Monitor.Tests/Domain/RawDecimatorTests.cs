using System;
using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class RawDecimatorTests
{
    static SeriesPoint Bucket(DateTime start, int minT, int maxT, int minH, int maxH)
        => new(start, AvgTemp: (minT + maxT) / 2.0, MinTemp: minT, MaxTemp: maxT,
               AvgHumidity: (minH + maxH) / 2.0, MinHumidity: minH, MaxHumidity: maxH, Count: 96);

    [Fact]
    public void MinMaxEnvelope_emits_the_recorded_min_and_max_per_bucket()
    {
        var series = new[] { Bucket(new DateTime(2026, 6, 15, 0, 0, 0), minT: 20, maxT: 23, minH: 44, maxH: 52) };

        var pts = RawDecimator.MinMaxEnvelope(series, bucketMinutes: 1440);

        Assert.Equal(2, pts.Count);
        Assert.Contains(pts, p => p.TemperatureC == 20 && p.HumidityPct == 44);  // recorded low
        Assert.Contains(pts, p => p.TemperatureC == 23 && p.HumidityPct == 52);  // recorded high
    }

    [Fact]
    public void MinMaxEnvelope_of_empty_series_is_empty()
        => Assert.Empty(RawDecimator.MinMaxEnvelope(Array.Empty<SeriesPoint>(), 1440));

    [Fact]
    public void MinMaxEnvelope_keeps_two_points_per_bucket_in_time_order()
    {
        var d0 = new DateTime(2026, 6, 1, 0, 0, 0);
        var series = new[]
        {
            Bucket(d0, minT: 20, maxT: 22, minH: 40, maxH: 50),
            Bucket(d0.AddDays(1), minT: 21, maxT: 24, minH: 41, maxH: 55),
        };

        var pts = RawDecimator.MinMaxEnvelope(series, bucketMinutes: 1440);

        Assert.Equal(4, pts.Count);
        Assert.True(pts[0].TimestampCet <= pts[1].TimestampCet && pts[1].TimestampCet <= pts[2].TimestampCet);
        Assert.Equal(d0, pts[0].TimestampCet);                 // min anchored at the bucket start
        Assert.Equal(d0.AddMinutes(720), pts[1].TimestampCet); // max at the bucket midpoint (1440 / 2)
    }
}
