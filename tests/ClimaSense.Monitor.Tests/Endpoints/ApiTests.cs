using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ClimaSense.Monitor.Tests.Endpoints;

sealed class ThrowingRepo : ISensorReadingRepository
{
    public Task<SensorReading?> GetLatestAsync(CancellationToken ct = default) => throw new TimeoutException("db down");
    public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime f, DateTime t, int b, CancellationToken ct = default) => throw new TimeoutException("db down");
    public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime f, DateTime t, CancellationToken ct = default) => throw new TimeoutException("db down");
    public Task<IReadOnlyList<SensorReading>> GetRawAsync(DateTime f, DateTime t, int max, CancellationToken ct = default) => throw new TimeoutException("db down");
}

public sealed class FakeRepo : ISensorReadingRepository
{
    public SensorReading? Latest = new(1, new DateTime(2026, 6, 15, 18, 0, 0), 19, 49);
    public Task<SensorReading?> GetLatestAsync(CancellationToken ct = default) => Task.FromResult(Latest);
    public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime f, DateTime t, int b, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<SeriesPoint>)new[] { new SeriesPoint(f, 19, 18, 20, 49, 48, 50, 4) });
    public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime f, DateTime t, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<DailyAggregate>)Array.Empty<DailyAggregate>());
    public Task<IReadOnlyList<SensorReading>> GetRawAsync(DateTime f, DateTime t, int max, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<SensorReading>)new[] { new SensorReading(1, new DateTime(2026, 6, 15, 18, 0, 0), 19, 49) });
}

sealed class BreachRepo : ISensorReadingRepository
{
    public Task<SensorReading?> GetLatestAsync(CancellationToken ct = default)
        => Task.FromResult<SensorReading?>(new SensorReading(1, new DateTime(2026, 6, 15, 18, 0, 0), 40, 49)); // 40 °C out of band
    public Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime f, DateTime t, int b, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<SeriesPoint>)new[] { new SeriesPoint(f, 40, 40, 40, 49, 49, 49, 4) });
    public Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime f, DateTime t, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<DailyAggregate>)Array.Empty<DailyAggregate>());
    public Task<IReadOnlyList<SensorReading>> GetRawAsync(DateTime f, DateTime t, int max, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<SensorReading>)Array.Empty<SensorReading>());
}

sealed class FixedClock(DateTime utc) : IClock { public DateTime UtcNow => utc; }

public sealed class MonitorFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Ups3", "Server=dummy;Database=dummy;");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISensorReadingRepository>();
            services.AddSingleton<ISensorReadingRepository>(new FakeRepo());
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new FixedClock(new DateTime(2026, 6, 15, 16, 10, 0, DateTimeKind.Utc)));
        });
    }
}

public class ApiTests(MonitorFactory factory) : IClassFixture<MonitorFactory>
{
    [Fact]
    public async Task Latest_returns_status_json()
    {
        var res = await factory.CreateClient().GetAsync("/api/readings/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<LatestDto>();
        Assert.Equal(19, body!.Reading.TemperatureC);
        Assert.False(body.IsStale);   // 10 min old < 30
    }

    [Fact]
    public async Task Series_with_preset_returns_points()
    {
        var res = await factory.CreateClient().GetAsync("/api/readings/series?range=24h");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pts = await res.Content.ReadFromJsonAsync<List<SeriesPoint>>();
        Assert.Single(pts!);
    }

    [Fact]
    public async Task Series_with_bad_range_is_400()
    {
        var res = await factory.CreateClient().GetAsync("/api/readings/series?range=3y");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Health_reports_ok()
    {
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);   // fresh -> Healthy
    }

    [Fact]
    public async Task Db_failure_returns_503()
    {
        var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ISensorReadingRepository>();
            s.AddSingleton<ISensorReadingRepository>(new ThrowingRepo());
        })).CreateClient();
        var res = await client.GetAsync("/api/readings/latest");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task Latest_is_200_with_null_when_no_data()
    {
        var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ISensorReadingRepository>();
            s.AddSingleton<ISensorReadingRepository>(new FakeRepo { Latest = null });
        })).CreateClient();
        var res = await client.GetAsync("/api/readings/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Health_degraded_when_feed_is_stale()
    {
        // FakeRepo.Latest is 2026-06-15 18:00 CET (16:00 UTC); this clock is ~8h later -> stale.
        var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IClock>();
            s.AddSingleton<IClock>(new FixedClock(new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc)));
        })).CreateClient();
        var res = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);               // Degraded is reported as 200
        Assert.Equal("Degraded", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Daily_with_preset_returns_ok()
        => Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/readings/daily?range=30d")).StatusCode);

    [Fact]
    public async Task Excursions_with_preset_returns_ok()
        => Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/readings/excursions?range=30d")).StatusCode);

    [Fact]
    public async Task Alerts_returns_breach_when_out_of_band()
    {
        var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ISensorReadingRepository>();
            s.AddSingleton<ISensorReadingRepository>(new BreachRepo());
        })).CreateClient();
        var res = await client.GetAsync("/api/alerts?range=24h");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var alerts = await res.Content.ReadFromJsonAsync<List<AlertDto>>();
        Assert.NotEmpty(alerts!);
        Assert.Equal("Breach", alerts![0].Kind);
    }

    [Fact]
    public async Task Insights_with_preset_returns_ok()
    {
        var res = await factory.CreateClient().GetAsync("/api/insights?range=7d");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ins = await res.Content.ReadFromJsonAsync<InsightsDto>();
        Assert.Equal("Temperature", ins!.Temperature.Metric);
    }

    [Fact]
    public async Task Raw_with_preset_returns_actual_points()
    {
        var res = await factory.CreateClient().GetAsync("/api/readings/raw?range=24h");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pts = await res.Content.ReadFromJsonAsync<List<RawDto>>();
        Assert.Single(pts!);
        Assert.Equal(19, pts![0].TemperatureC);
    }

    [Fact]
    public async Task Raw_with_oversized_range_returns_the_minmax_envelope()
    {
        var res = await factory.CreateClient().GetAsync("/api/readings/raw?range=all");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pts = await res.Content.ReadFromJsonAsync<List<RawDto>>();
        Assert.Equal(2, pts!.Count);                                              // one series bucket -> min + max
        Assert.Contains(pts, p => p.TemperatureC == 18 && p.HumidityPct == 48);   // recorded min
        Assert.Contains(pts, p => p.TemperatureC == 20 && p.HumidityPct == 50);   // recorded max
    }

    sealed record RawDto(DateTime TimestampCet, int TemperatureC, int HumidityPct);
    sealed record LatestDto(SensorReading Reading, bool IsStale);
    sealed record AlertDto(string Kind, string? Metric, DateTime StartCet, DateTime? EndCet, string Severity, string Message);
    sealed record InsightsDto(MetricInsightDto Temperature);
    sealed record MetricInsightDto(string Metric);
}
