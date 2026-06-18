using System;
using System.Threading.Tasks;
using ClimaSense.Monitor.Data;
using Xunit;

namespace ClimaSense.Monitor.Tests.Integration;

[Trait("Category", "Integration")]
public class SqlSensorReadingRepositoryTests
{
    static string? Conn => Environment.GetEnvironmentVariable("CLIMASENSE_UPS3_CONNECTION");

    [SkippableFact]
    public async Task GetLatest_returns_a_reading()
    {
        Skip.If(string.IsNullOrEmpty(Conn), "CLIMASENSE_UPS3_CONNECTION not set");
        var repo = new SqlSensorReadingRepository(Conn!);
        var r = await repo.GetLatestAsync();
        Assert.NotNull(r);
        Assert.True(r!.Value.Id > 0);
    }

    [SkippableFact]
    public async Task GetSeries_last24h_returns_buckets()
    {
        Skip.If(string.IsNullOrEmpty(Conn), "CLIMASENSE_UPS3_CONNECTION not set");
        var repo = new SqlSensorReadingRepository(Conn!);
        var now = DateTime.Now;                       // CET wall-clock on the dev box
        var series = await repo.GetSeriesAsync(now.AddHours(-24), now, 15);
        Assert.NotEmpty(series);
    }

    [SkippableFact]
    public async Task GetDaily_last7d_returns_days()
    {
        Skip.If(string.IsNullOrEmpty(Conn), "CLIMASENSE_UPS3_CONNECTION not set");
        var repo = new SqlSensorReadingRepository(Conn!);
        var now = DateTime.Now;
        var days = await repo.GetDailyAggregatesAsync(now.AddDays(-7), now);
        Assert.NotEmpty(days);
    }

    [SkippableFact]
    public async Task GetRaw_last24h_returns_actual_rows()
    {
        Skip.If(string.IsNullOrEmpty(Conn), "CLIMASENSE_UPS3_CONNECTION not set");
        var repo = new SqlSensorReadingRepository(Conn!);
        var now = DateTime.Now;
        var raw = await repo.GetRawAsync(now.AddHours(-24), now, 5000);
        Assert.NotEmpty(raw);
        Assert.True(raw[0].Id > 0);
    }
}
