using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using Microsoft.Extensions.Options;

namespace ClimaSense.Monitor.Services;

public readonly record struct MetricInsight(
    string Metric,
    DriftDirection Drift,
    IReadOnlyList<AnomalyPoint> Anomalies,
    SensorStatus SensorHealth,
    IReadOnlyList<double> Forecast,
    int? StepsToBreach);

public readonly record struct Insights(
    MetricInsight Temperature,
    MetricInsight Humidity,
    double DewPointC,
    double CondensationMarginC);

/// <summary>Composes the four read-only analyzers over a fetched window into one Insights view.</summary>
public sealed class InsightsService(ISensorReadingRepository repo, IOptions<EnvelopeOptions> options)
{
    readonly EnvelopeOptions _env = options.Value;

    public async Task<Insights?> GetInsightsAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
    {
        int bucket = BucketSelector.BucketMinutes(toCet - fromCet);
        var series = await repo.GetSeriesAsync(fromCet, toCet, bucket, ct);
        var latest = await repo.GetLatestAsync(ct);
        if (latest is null) return null;
        var r = latest.Value;

        return new Insights(
            BuildMetric("Temperature", series, Metric.Temperature, _env.TemperatureRecommended.Max),
            BuildMetric("Humidity", series, Metric.Humidity, _env.HumidityRecommended.Max),
            Psychrometrics.DewPointC(r.TemperatureC, r.HumidityPct),
            Psychrometrics.CondensationMarginC(r.TemperatureC, r.HumidityPct));
    }

    static MetricInsight BuildMetric(string name, IReadOnlyList<SeriesPoint> series, Metric metric, double upperLimit)
    {
        var drift = DriftDetector.Analyze(series, metric);
        return new MetricInsight(
            name,
            drift.Direction,
            drift.Anomalies,
            SensorHealth.Evaluate(series, metric),
            Forecaster.Forecast(series, metric, 6),
            Forecaster.StepsToThreshold(series, metric, upperLimit));
    }
}
