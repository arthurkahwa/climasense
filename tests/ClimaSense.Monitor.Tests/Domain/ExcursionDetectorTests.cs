using System;
using System.Collections.Generic;
using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class ExcursionDetectorTests
{
    static readonly EnvelopeRange Rec = new() { Min = 18, Max = 27 };
    static readonly EnvelopeRange All = new() { Min = 15, Max = 32 };

    static SeriesPoint P(int hour, double temp) =>
        new(new DateTime(2026, 6, 15, hour, 0, 0), temp, (int)temp, (int)temp, 50, 50, 50, 4);

    [Fact]
    public void Detect_finds_one_contiguous_run_with_peak_and_band()
    {
        var series = new List<SeriesPoint> { P(0, 20), P(1, 30), P(2, 35), P(3, 20) }; // run at 01:00-02:00
        var ex = ExcursionDetector.Detect(series, Metric.Temperature, 60, Rec, All);

        var e = Assert.Single(ex);
        Assert.Equal(new DateTime(2026, 6, 15, 1, 0, 0), e.StartCet);
        Assert.Equal(new DateTime(2026, 6, 15, 3, 0, 0), e.EndCet); // last bucket start (02:00) + 60 min
        Assert.Equal(120, e.DurationMinutes);
        Assert.Equal(35, e.Peak);                  // furthest above recommended max (27)
        Assert.Equal(ReadingBand.OutOfRange, e.Band);
    }

    [Fact]
    public void Detect_returns_empty_when_all_recommended()
        => Assert.Empty(ExcursionDetector.Detect(
            new List<SeriesPoint> { P(0, 20), P(1, 21) }, Metric.Temperature, 60, Rec, All));
}
