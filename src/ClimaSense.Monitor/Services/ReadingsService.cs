using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using Microsoft.Extensions.Options;

namespace ClimaSense.Monitor.Services;

public sealed class ReadingsService(ISensorReadingRepository repo, IOptions<EnvelopeOptions> options, IClock clock)
{
    readonly EnvelopeOptions _env = options.Value;

    public async Task<LatestStatus?> GetLatestStatusAsync(CancellationToken ct = default)
    {
        var r = await repo.GetLatestAsync(ct);
        if (r is null) return null;
        var reading = r.Value;
        var t = BandEvaluator.Classify(reading.TemperatureC, _env.TemperatureRecommended, _env.TemperatureAllowable);
        var h = BandEvaluator.Classify(reading.HumidityPct, _env.HumidityRecommended, _env.HumidityAllowable);
        var nowUtc = clock.UtcNow;
        return new LatestStatus(reading, t, h, BandEvaluator.Worst(t, h),
            Freshness.MinutesOld(reading.Timestamp, nowUtc),
            Freshness.IsStale(reading.Timestamp, nowUtc, _env.FreshnessMinutes));
    }

    public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
        => repo.GetSeriesAsync(fromCet, toCet, BucketSelector.BucketMinutes(toCet - fromCet), ct);

    public Task<IReadOnlyList<DailyAggregate>> GetDailyAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
        => repo.GetDailyAggregatesAsync(fromCet, toCet, ct);

    public const int MaxRawDays = 210;        // up to here: every reading (~20k at the 15-min cadence); beyond: min/max-decimated
    public const int MaxRawPoints = 25000;    // hard safety cap for the individual-reading path

    /// <summary>
    /// Actual (un-averaged) readings for the Messwerte view. Up to <see cref="MaxRawDays"/>
    /// every reading is returned; beyond that the window holds too many points to plot, so
    /// it min/max-decimates — two recorded readings (min and max) per server-side bucket —
    /// keeping real extremes while staying a few hundred points.
    /// </summary>
    public async Task<IReadOnlyList<RawPoint>> GetRawAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
    {
        var span = toCet - fromCet;
        if (span.TotalDays > MaxRawDays)
        {
            int bucket = BucketSelector.BucketMinutes(span);
            var series = await repo.GetSeriesAsync(fromCet, toCet, bucket, ct);
            return RawDecimator.MinMaxEnvelope(series, bucket);
        }
        var rows = await repo.GetRawAsync(fromCet, toCet, MaxRawPoints, ct);
        return rows.Select(r => new RawPoint(r.Timestamp, r.TemperatureC, r.HumidityPct)).ToList();
    }

    public async Task<IReadOnlyList<Excursion>> GetExcursionsAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
    {
        int bucket = BucketSelector.BucketMinutes(toCet - fromCet);
        var series = await repo.GetSeriesAsync(fromCet, toCet, bucket, ct);
        var temp = ExcursionDetector.Detect(series, Metric.Temperature, bucket, _env.TemperatureRecommended, _env.TemperatureAllowable);
        var hum  = ExcursionDetector.Detect(series, Metric.Humidity, bucket, _env.HumidityRecommended, _env.HumidityAllowable);
        return temp.Concat(hum).OrderByDescending(e => e.StartCet).ToList();
    }

    public async Task<IReadOnlyList<Alert>> GetAlertsAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
    {
        int bucket = BucketSelector.BucketMinutes(toCet - fromCet);
        var series = await repo.GetSeriesAsync(fromCet, toCet, bucket, ct);
        var latest = await repo.GetLatestAsync(ct);
        return AlertEvaluator.Evaluate(series, bucket, latest, clock.UtcNow, _env);
    }
}
