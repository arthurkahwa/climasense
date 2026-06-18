namespace ClimaSense.Monitor.Domain;

public enum Metric { Temperature, Humidity }

public readonly record struct Excursion(
    Metric Metric, DateTime StartCet, DateTime EndCet, int DurationMinutes, double Peak, ReadingBand Band);

public static class ExcursionDetector
{
    /// <summary>Maximal contiguous runs where the metric leaves the Recommended band.</summary>
    public static IReadOnlyList<Excursion> Detect(
        IReadOnlyList<SeriesPoint> series, Metric metric, int bucketMinutes, EnvelopeRange recommended, EnvelopeRange allowable)
    {
        var result = new List<Excursion>();
        int runStart = -1;
        for (int i = 0; i < series.Count; i++)
        {
            var band = BandEvaluator.Classify(Value(series[i], metric), recommended, allowable);
            bool inRun = band != ReadingBand.Recommended;
            if (inRun && runStart < 0) runStart = i;
            else if (!inRun && runStart >= 0) { result.Add(Build(series, metric, bucketMinutes, recommended, allowable, runStart, i - 1)); runStart = -1; }
        }
        if (runStart >= 0) result.Add(Build(series, metric, bucketMinutes, recommended, allowable, runStart, series.Count - 1));
        return result;
    }

    static double Value(SeriesPoint p, Metric m) => m == Metric.Temperature ? p.AvgTemp : p.AvgHumidity;

    static Excursion Build(IReadOnlyList<SeriesPoint> s, Metric metric, int bucketMinutes,
        EnvelopeRange recommended, EnvelopeRange allowable, int from, int to)
    {
        var start = s[from].BucketStartCet;
        var end = s[to].BucketStartCet.AddMinutes(bucketMinutes);
        double peak = recommended.Min, maxDist = -1;
        var worst = ReadingBand.Recommended;
        for (int i = from; i <= to; i++)
        {
            double v = Value(s[i], metric);
            worst = BandEvaluator.Worst(worst, BandEvaluator.Classify(v, recommended, allowable));
            double dist = v < recommended.Min ? recommended.Min - v : v > recommended.Max ? v - recommended.Max : 0;
            if (dist > maxDist) { maxDist = dist; peak = v; }
        }
        return new Excursion(metric, start, end, (int)(end - start).TotalMinutes, peak, worst);
    }
}
