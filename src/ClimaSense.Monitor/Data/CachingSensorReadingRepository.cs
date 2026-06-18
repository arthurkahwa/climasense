using ClimaSense.Monitor.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace ClimaSense.Monitor.Data;

public sealed class CachingSensorReadingRepository(ISensorReadingRepository inner, IMemoryCache cache) : ISensorReadingRepository
{
    public async Task<SensorReading?> GetLatestAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue("latest", out SensorReading cached)) return cached;
        var r = await inner.GetLatestAsync(ct);
        if (r is not null) cache.Set("latest", r.Value, TimeSpan.FromSeconds(30));
        return r;
    }

    public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime fromCet, DateTime toCet, int bucketMinutes, CancellationToken ct = default)
        => cache.GetOrCreateAsync($"series:{fromCet:o}:{toCet:o}:{bucketMinutes}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return inner.GetSeriesAsync(fromCet, toCet, bucketMinutes, ct);
        })!;

    public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
        => cache.GetOrCreateAsync($"daily:{fromCet:o}:{toCet:o}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return inner.GetDailyAggregatesAsync(fromCet, toCet, ct);
        })!;

    public Task<IReadOnlyList<SensorReading>> GetRawAsync(DateTime fromCet, DateTime toCet, int maxPoints, CancellationToken ct = default)
        => cache.GetOrCreateAsync($"raw:{fromCet:o}:{toCet:o}:{maxPoints}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return inner.GetRawAsync(fromCet, toCet, maxPoints, ct);
        })!;
}
