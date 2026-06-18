namespace ClimaSense.Monitor.Domain;

public enum DriftDirection { Rising, Falling, Stable }

public readonly record struct AnomalyPoint(DateTime BucketStartCet, double Value, double Score);

public readonly record struct DriftReport(
    IReadOnlyList<AnomalyPoint> Anomalies,
    DriftDirection Direction,
    double RecentMean,
    double EarlierMean);

public static class DriftDetector
{
    public static DriftReport Analyze(
        IReadOnlyList<SeriesPoint> series,
        Metric metric,
        double k = 2.5,
        double driftThreshold = 1.0)
    {
        if (series.Count == 0)
            return new DriftReport([], DriftDirection.Stable, 0, 0);

        var values = series.Select(p => metric == Metric.Temperature ? p.AvgTemp : p.AvgHumidity).ToArray();

        double mean = values.Average();
        double variance = values.Average(v => (v - mean) * (v - mean));
        double std = Math.Sqrt(variance);

        var anomalies = std == 0
            ? (IReadOnlyList<AnomalyPoint>)[]
            : values.Select((v, i) => (v, i))
                    .Where(x => Math.Abs(x.v - mean) > k * std)
                    .Select(x => new AnomalyPoint(series[x.i].BucketStartCet, x.v, (x.v - mean) / std))
                    .ToList();

        int half = series.Count / 2;
        double earlierMean = half > 0 ? values.Take(half).Average() : mean;
        double recentMean  = half < values.Length ? values.Skip(half).Average() : mean;

        double diff = recentMean - earlierMean;
        DriftDirection direction = diff > driftThreshold
            ? DriftDirection.Rising
            : diff < -driftThreshold
                ? DriftDirection.Falling
                : DriftDirection.Stable;

        return new DriftReport(anomalies, direction, recentMean, earlierMean);
    }
}
