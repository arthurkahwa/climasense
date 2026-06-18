using System;
using System.Collections.Generic;
using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class AlertEvaluatorTests
{
    static readonly EnvelopeOptions Env = new();   // ASHRAE defaults: temp 18-27, hum 20-80, fresh 30 min
    const int Bucket = 60;

    // An aggregated bucket; defaults are comfortably in-envelope.
    static SeriesPoint P(int hour, double temp = 20, double hum = 50) =>
        new(new DateTime(2026, 6, 15, hour, 0, 0), temp, (int)temp, (int)temp, hum, (int)hum, (int)hum, 4);

    [Fact]
    public void No_alerts_when_in_envelope_and_fresh()
    {
        var series = new List<SeriesPoint> { P(10), P(11), P(12) };
        var latest = new SensorReading(1, new DateTime(2026, 6, 15, 12, 0, 0), 20, 50); // 12:00 CET = 10:00 UTC
        var nowUtc = new DateTime(2026, 6, 15, 10, 10, 0, DateTimeKind.Utc);             // 10 min later -> fresh

        var alerts = AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Closed_breach_emits_one_breach_alert()
    {
        var series = new List<SeriesPoint> { P(10, 20), P(11, 35), P(12, 35), P(13, 20), P(14, 20) };
        var latest = new SensorReading(9, new DateTime(2026, 6, 15, 14, 0, 0), 20, 50); // in-band, recent
        var nowUtc = new DateTime(2026, 6, 15, 12, 5, 0, DateTimeKind.Utc);             // 14:05 CET -> fresh

        var alerts = AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env);

        var a = Assert.Single(alerts, x => x.Kind == AlertKind.Breach);
        Assert.Equal(AlertKind.Breach, a.Kind);
        Assert.Equal(Metric.Temperature, a.Metric);
        Assert.Equal(new DateTime(2026, 6, 15, 11, 0, 0), a.StartCet);
        Assert.Equal(new DateTime(2026, 6, 15, 13, 0, 0), a.EndCet); // last breach bucket (12:00) + 60 min
        Assert.Equal(ReadingBand.OutOfRange, a.Severity);
    }

    [Fact]
    public void Active_breach_has_null_end()
    {
        var series = new List<SeriesPoint> { P(10, 20), P(11, 20), P(12, 35), P(13, 35) }; // breach ongoing at series end
        var latest = new SensorReading(9, new DateTime(2026, 6, 15, 13, 0, 0), 35, 50);
        var nowUtc = new DateTime(2026, 6, 15, 11, 5, 0, DateTimeKind.Utc);                 // 13:05 CET -> fresh

        var alerts = AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env);

        var a = Assert.Single(alerts, x => x.Kind == AlertKind.Breach);
        Assert.Equal(AlertKind.Breach, a.Kind);
        Assert.Equal(new DateTime(2026, 6, 15, 12, 0, 0), a.StartCet);
        Assert.Null(a.EndCet); // active / ongoing
    }

    [Fact]
    public void Stale_feed_emits_stale_alert()
    {
        var series = new List<SeriesPoint> { P(10, 20), P(11, 20) };                    // all in-band -> no breach
        var latest = new SensorReading(9, new DateTime(2026, 6, 15, 11, 0, 0), 20, 50); // 11:00 CET = 09:00 UTC
        var nowUtc = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);             // 60 min later -> stale

        var alerts = AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env);

        var a = Assert.Single(alerts);
        Assert.Equal(AlertKind.StaleFeed, a.Kind);
        Assert.Null(a.Metric);
        Assert.Equal(new DateTime(2026, 6, 15, 11, 0, 0), a.StartCet); // since the last reading
        Assert.Null(a.EndCet);
    }

    [Fact]
    public void Allowable_run_has_allowable_severity()
    {
        // 16 °C is below recommended (18) but within allowable (15–32) -> Allowable band.
        var series = new List<SeriesPoint> { P(10, 20), P(11, 16), P(12, 16), P(13, 20) };
        var latest = new SensorReading(9, new DateTime(2026, 6, 15, 13, 0, 0), 20, 50);
        var nowUtc = new DateTime(2026, 6, 15, 11, 5, 0, DateTimeKind.Utc); // fresh

        var a = Assert.Single(AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env));
        Assert.Equal(AlertKind.Breach, a.Kind);
        Assert.Equal(ReadingBand.Allowable, a.Severity);
    }

    [Fact]
    public void Multiple_breaches_ordered_newest_first()
    {
        var series = new List<SeriesPoint> { P(10, 20), P(11, 35), P(12, 20), P(13, 35), P(14, 20) }; // two breaches
        var latest = new SensorReading(9, new DateTime(2026, 6, 15, 14, 0, 0), 20, 50);
        var nowUtc = new DateTime(2026, 6, 15, 12, 5, 0, DateTimeKind.Utc); // fresh

        var breaches = AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env)
            .Where(x => x.Kind == AlertKind.Breach).ToList();

        Assert.Equal(2, breaches.Count);
        Assert.Equal(new DateTime(2026, 6, 15, 13, 0, 0), breaches[0].StartCet); // newest first
        Assert.Equal(new DateTime(2026, 6, 15, 11, 0, 0), breaches[1].StartCet);
    }

    [Fact]
    public void In_band_statistical_anomaly_emits_anomaly_alert()
    {
        // 24 °C is within the recommended band (18–27) but far from the 20 °C baseline → an anomaly, not a breach.
        var series = Enumerable.Range(0, 9).Select(h => P(h, 20)).Append(P(9, 24)).ToList();
        var latest = new SensorReading(9, new DateTime(2026, 6, 15, 9, 0, 0), 24, 50);
        var nowUtc = new DateTime(2026, 6, 15, 7, 5, 0, DateTimeKind.Utc); // fresh

        var alerts = AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env);

        Assert.Contains(alerts, a => a.Kind == AlertKind.Anomaly && a.Metric == Metric.Temperature);
        Assert.DoesNotContain(alerts, a => a.Kind == AlertKind.Breach); // 24 is in-band → no breach, only the anomaly
    }

    [Fact]
    public void Sensor_spike_emits_sensor_alert()
    {
        var series = new List<SeriesPoint> { P(0, 20), P(1, 20), P(2, 20), P(3, 35), P(4, 20), P(5, 20) }; // 15° jump
        var latest = new SensorReading(5, new DateTime(2026, 6, 15, 5, 0, 0), 20, 50);
        var nowUtc = new DateTime(2026, 6, 15, 3, 5, 0, DateTimeKind.Utc); // fresh
        Assert.Contains(AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env), a => a.Kind == AlertKind.Sensor);
    }

    [Fact]
    public void Rising_trend_emits_forecast_alert()
    {
        var temps = new[] { 24.0, 24.5, 25.0, 25.5, 26.0 };               // slope 0.5 -> ~2 intervals to 27 (recommended max)
        var series = temps.Select((t, i) => P(i, t)).ToList();
        var latest = new SensorReading(5, new DateTime(2026, 6, 15, 4, 0, 0), 26, 50);
        var nowUtc = new DateTime(2026, 6, 15, 2, 5, 0, DateTimeKind.Utc); // fresh
        Assert.Contains(AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env),
            a => a.Kind == AlertKind.Forecast && a.Metric == Metric.Temperature);
    }

    [Fact]
    public void High_humidity_emits_condensation_alert()
    {
        var series = new List<SeriesPoint> { P(0, 20), P(1, 20), P(2, 20) };
        var latest = new SensorReading(3, new DateTime(2026, 6, 15, 3, 0, 0), 20, 98); // dew point ~19.7 -> margin < 3
        var nowUtc = new DateTime(2026, 6, 15, 1, 5, 0, DateTimeKind.Utc); // fresh
        Assert.Contains(AlertEvaluator.Evaluate(series, Bucket, latest, nowUtc, Env), a => a.Kind == AlertKind.Condensation);
    }
}
