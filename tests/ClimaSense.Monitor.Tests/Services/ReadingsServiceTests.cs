using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using ClimaSense.Monitor.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClimaSense.Monitor.Tests.Services;

public class ReadingsServiceTests
{
    sealed class FakeRepo : ISensorReadingRepository
    {
        public SensorReading? Latest;
        public int GetLatestCalls;
        public Task<SensorReading?> GetLatestAsync(CancellationToken ct = default) { GetLatestCalls++; return Task.FromResult(Latest); }
        public IReadOnlyList<SeriesPoint> Series = Array.Empty<SeriesPoint>();
        public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime f, DateTime t, int b, CancellationToken ct = default)
            => Task.FromResult(Series);
        public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime f, DateTime t, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<DailyAggregate>)Array.Empty<DailyAggregate>());
        public IReadOnlyList<SensorReading> Raw = Array.Empty<SensorReading>();
        public Task<IReadOnlyList<SensorReading>> GetRawAsync(DateTime f, DateTime t, int max, CancellationToken ct = default)
            => Task.FromResult(Raw);
    }
    sealed class FixedClock(DateTime utc) : IClock { public DateTime UtcNow => utc; }

    static ReadingsService Build(FakeRepo repo, DateTime nowUtc)
        => new(repo, Options.Create(new EnvelopeOptions()), new FixedClock(nowUtc));

    [Fact]
    public async Task LatestStatus_classifies_bands_and_flags_stale()
    {
        var repo = new FakeRepo { Latest = new SensorReading(1, new DateTime(2026, 6, 15, 18, 0, 0), 35, 50) };
        var s = await Build(repo, new DateTime(2026, 6, 15, 16, 45, 0, DateTimeKind.Utc)).GetLatestStatusAsync();

        Assert.NotNull(s);
        Assert.Equal(ReadingBand.OutOfRange, s.Value.TempBand);
        Assert.Equal(ReadingBand.Recommended, s.Value.HumidityBand);
        Assert.Equal(ReadingBand.OutOfRange, s.Value.Overall);
        Assert.True(s.Value.IsStale);
    }

    [Fact]
    public async Task LatestStatus_null_when_no_data()
        => Assert.Null(await Build(new FakeRepo(), DateTime.UtcNow).GetLatestStatusAsync());

    [Fact]
    public async Task Raw_maps_actual_readings_for_a_small_range()
    {
        var repo = new FakeRepo { Raw = new[] { new SensorReading(1, new DateTime(2026, 6, 15, 18, 0, 0), 21, 47) } };
        var pts = await Build(repo, DateTime.UtcNow).GetRawAsync(new DateTime(2026, 6, 15), new DateTime(2026, 6, 16));
        Assert.NotNull(pts);
        var p = Assert.Single(pts);
        Assert.Equal(21, p.TemperatureC);
        Assert.Equal(47, p.HumidityPct);
    }

    [Fact]
    public async Task Raw_decimates_to_a_minmax_envelope_when_range_exceeds_the_cap()
    {
        var repo = new FakeRepo
        {
            Series = new[]
            {
                new SeriesPoint(new DateTime(2025, 1, 1), AvgTemp: 21.5, MinTemp: 20, MaxTemp: 23,
                                AvgHumidity: 47, MinHumidity: 44, MaxHumidity: 52, Count: 96),
            },
        };
        // 2 years > MaxRawDays -> actual min/max readings, not null
        var pts = await Build(repo, DateTime.UtcNow).GetRawAsync(new DateTime(2024, 1, 1), new DateTime(2026, 1, 1));

        Assert.NotNull(pts);
        Assert.Equal(2, pts.Count);   // one bucket -> recorded min + max
        Assert.Contains(pts, p => p.TemperatureC == 20 && p.HumidityPct == 44);
        Assert.Contains(pts, p => p.TemperatureC == 23 && p.HumidityPct == 52);
    }
}
