using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace ClimaSense.Monitor.Tests.Data;

public class CachingSensorReadingRepositoryTests
{
    sealed class CountingRepo : ISensorReadingRepository
    {
        public int LatestCalls;
        public int RawCalls;
        public Task<SensorReading?> GetLatestAsync(CancellationToken ct = default)
        { LatestCalls++; return Task.FromResult<SensorReading?>(new SensorReading(1, DateTime.Now, 20, 50)); }
        public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime f, DateTime t, int b, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<SeriesPoint>)Array.Empty<SeriesPoint>());
        public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime f, DateTime t, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<DailyAggregate>)Array.Empty<DailyAggregate>());
        public Task<IReadOnlyList<SensorReading>> GetRawAsync(DateTime f, DateTime t, int max, CancellationToken ct = default)
        { RawCalls++; return Task.FromResult((IReadOnlyList<SensorReading>)Array.Empty<SensorReading>()); }
    }

    [Fact]
    public async Task GetLatest_is_cached()
    {
        var inner = new CountingRepo();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CachingSensorReadingRepository(inner, cache);
        await sut.GetLatestAsync();
        await sut.GetLatestAsync();
        Assert.Equal(1, inner.LatestCalls);
    }

    [Fact]
    public async Task GetRaw_is_cached_per_range()
    {
        var inner = new CountingRepo();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CachingSensorReadingRepository(inner, cache);
        var from = new DateTime(2026, 6, 15); var to = new DateTime(2026, 6, 16);
        await sut.GetRawAsync(from, to, 25000);
        await sut.GetRawAsync(from, to, 25000);
        Assert.Equal(1, inner.RawCalls);
    }
}
