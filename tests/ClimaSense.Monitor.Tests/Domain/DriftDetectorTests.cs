using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class DriftDetectorTests
{
    static SeriesPoint P(int hour, double temp, double hum = 50) =>
        new(new DateTime(2026, 6, 15, hour, 0, 0),
            temp, (int)temp, (int)temp,
            hum, (int)hum, (int)hum,
            4);

    [Fact]
    public void Flat_series_has_no_anomalies_and_is_stable()
    {
        var series = Enumerable.Range(0, 8).Select(h => P(h, 20)).ToList();
        var report = DriftDetector.Analyze(series, Metric.Temperature);

        Assert.Empty(report.Anomalies);
        Assert.Equal(DriftDirection.Stable, report.Direction);
    }

    [Fact]
    public void Single_spike_is_detected_as_one_anomaly_with_correct_value()
    {
        // 9 points at 20, 1 point at 60 — mean=24, std=12, score for 60 = (60-24)/12 = 3.0 > 2.5
        var series = Enumerable.Range(0, 9).Select(h => P(h, 20))
            .Append(P(9, 60))
            .ToList();
        var report = DriftDetector.Analyze(series, Metric.Temperature);

        var anomaly = Assert.Single(report.Anomalies);
        Assert.Equal(60, anomaly.Value);
    }

    [Fact]
    public void Rising_temps_produce_rising_direction_with_no_anomalies()
    {
        // earlier half [18,18,19,19] mean=18.5, recent half [20,20,21,21] mean=20.5, diff=2.0 > 1.0
        var temps = new[] { 18, 18, 19, 19, 20, 20, 21, 21 };
        var series = temps.Select((t, i) => P(i, t)).ToList();
        var report = DriftDetector.Analyze(series, Metric.Temperature);

        Assert.Equal(DriftDirection.Rising, report.Direction);
        Assert.Empty(report.Anomalies);
    }

    [Fact]
    public void Falling_temps_produce_falling_direction()
    {
        // reverse of rising: earlier half [21,21,20,20] mean=20.5, recent half [19,19,18,18] mean=18.5, diff=-2.0 < -1.0
        var temps = new[] { 21, 21, 20, 20, 19, 19, 18, 18 };
        var series = temps.Select((t, i) => P(i, t)).ToList();
        var report = DriftDetector.Analyze(series, Metric.Temperature);

        Assert.Equal(DriftDirection.Falling, report.Direction);
    }

    [Fact]
    public void Short_series_does_not_throw_and_is_stable()
    {
        // half-split must not call .Average() on an empty half (a 1-point series -> half == 0).
        Assert.Equal(DriftDirection.Stable, DriftDetector.Analyze(new List<SeriesPoint> { P(0, 20) }, Metric.Temperature).Direction);
        Assert.Empty(DriftDetector.Analyze(new List<SeriesPoint>(), Metric.Temperature).Anomalies);
    }
}
