namespace ClimaSense.Monitor.Domain;

public static class RawDecimator
{
    /// <summary>
    /// Min-max decimation for the Messwerte (raw) view over wide windows that hold
    /// too many readings to plot individually. Emits two <em>actual recorded</em>
    /// readings per bucket — the min and the max — so the chart shows real extremes
    /// (not averages), bounded to ~2× the bucket count.
    /// </summary>
    public static IReadOnlyList<RawPoint> MinMaxEnvelope(IReadOnlyList<SeriesPoint> series, int bucketMinutes)
    {
        var result = new List<RawPoint>(series.Count * 2);
        foreach (var p in series)
        {
            result.Add(new RawPoint(p.BucketStartCet, p.MinTemp, p.MinHumidity));
            result.Add(new RawPoint(p.BucketStartCet.AddMinutes(bucketMinutes / 2.0), p.MaxTemp, p.MaxHumidity));
        }
        return result;
    }
}
