using ClimaSense.Monitor.Domain;

namespace ClimaSense.Monitor.Data;

public interface ISensorReadingRepository
{
    Task<SensorReading?> GetLatestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime fromCet, DateTime toCet, int bucketMinutes, CancellationToken ct = default);
    Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default);
    Task<IReadOnlyList<SensorReading>> GetRawAsync(DateTime fromCet, DateTime toCet, int maxPoints, CancellationToken ct = default);
}
