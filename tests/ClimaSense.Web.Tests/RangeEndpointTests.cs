// SPDX-License-Identifier: MIT
//
// Slice-4 endpoint integration tests for:
//   * GET /api/readings/range
//   * GET /api/readings/heatmap
//
// Mirrors the slice-3 `LatestReadingEndpointTests` pattern: replaces
// the production `RangeQueryService` with one wired to per-test
// fetcher lambdas via a custom WebApplicationFactory. We never need a
// live SQL Server in the in-memory test host.
//
// AC coverage (issue #6):
//   * "GET /api/readings/range?start=...&end=...&bucket=hour returns
//      168 rows (one per hour) with non-null temperature values"
//      — `Range_returns_168_hourly_buckets_for_one_week`.
//   * "GET /api/readings/range?bucket=raw is 1-minute resolution"
//      — `Range_raw_passes_rows_through_unchanged`.
//   * "GET /api/readings/heatmap?year=2024 returns up to 366 daily
//      mean rows" — `Heatmap_returns_366_cells_for_leap_year`.
//   * "Range queries apply WHERE ReadingTime <= @as_of_time from
//      IClock.Now()" — `Range_passes_cursor_AsOf_to_the_fetcher`.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Readings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class RangeEndpointTests
    : IClassFixture<RangeEndpointTests.Factory>
{
    private readonly Factory _factory;

    public RangeEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------
    // Custom WebApplicationFactory: replaces RangeQueryService with a
    // mock-fetcher-backed instance per test.
    // -----------------------------------------------------------------
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public RangeFetcher RangeFetcher { get; set; } =
            (bucket, start, end, asOf, ct) =>
                Task.FromResult<IReadOnlyList<BucketedReading>>(Array.Empty<BucketedReading>());

        public HeatmapFetcher HeatmapFetcher { get; set; } =
            (yearStart, yearEnd, asOf, ct) =>
                Task.FromResult<IReadOnlyList<HeatmapCell>>(Array.Empty<HeatmapCell>());

        public int RawMaxDays { get; set; } = RangeQueryService.DefaultRawMaxDays;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(RangeQueryService))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddScoped<RangeQueryService>(_ =>
                    new RangeQueryService(
                        (bucket, start, end, asOf, ct) =>
                            RangeFetcher(bucket, start, end, asOf, ct),
                        (yearStart, yearEnd, asOf, ct) =>
                            HeatmapFetcher(yearStart, yearEnd, asOf, ct),
                        RawMaxDays));
            });
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // -----------------------------------------------------------------
    // Range — happy path (1 week of hourly buckets).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Range_returns_168_hourly_buckets_for_one_week()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc);

        // Fake fetcher: 168 populated buckets — one per hour.
        var fakeRows = Enumerable.Range(0, 168)
            .Select(h => new BucketedReading(
                BucketTime: start.AddHours(h),
                SampleCount: 12,
                TemperatureMean: 20.0 + (h % 5),
                TemperatureMin:  19.0 + (h % 5),
                TemperatureMax:  21.0 + (h % 5),
                HumidityMean:    40.0,
                HumidityMin:     38.0,
                HumidityMax:     42.0))
            .ToList();
        _factory.RangeFetcher = (bucket, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<BucketedReading>>(fakeRows);

        var client = _factory.CreateClient();
        var url = "/api/readings/range" +
            "?start=" + Uri.EscapeDataString(start.ToString("O")) +
            "&end=" + Uri.EscapeDataString(end.ToString("O")) +
            "&bucket=hour";
        var resp = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await JsonSerializer.DeserializeAsync<WireRangeResponse>(
            await resp.Content.ReadAsStreamAsync(), JsonOpts);

        Assert.NotNull(body);
        Assert.Equal("hour", body!.Bucket);
        // The endpoint validates + densifies; densification keeps the
        // 168 hourly slots even if the fetcher returns fewer rows.
        Assert.Equal(168, body.Buckets.Count);
        Assert.All(body.Buckets, b =>
        {
            Assert.NotNull(b.TemperatureMean);
            Assert.True(b.SampleCount > 0);
        });
    }

    // -----------------------------------------------------------------
    // Range — raw path (rows passed through, no densification).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Range_raw_passes_rows_through_unchanged()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(1);
        var fakeRows = Enumerable.Range(0, 12).Select(i => new BucketedReading(
                BucketTime: start.AddMinutes(i * 5),
                SampleCount: 1,
                TemperatureMean: 21.0,
                TemperatureMin: 21.0,
                TemperatureMax: 21.0,
                HumidityMean: 40.0,
                HumidityMin: 40.0,
                HumidityMax: 40.0))
            .ToList();
        _factory.RangeFetcher = (bucket, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<BucketedReading>>(fakeRows);

        var client = _factory.CreateClient();
        var url = "/api/readings/range" +
            "?start=" + Uri.EscapeDataString(start.ToString("O")) +
            "&end=" + Uri.EscapeDataString(end.ToString("O")) +
            "&bucket=raw";
        var resp = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await JsonSerializer.DeserializeAsync<WireRangeResponse>(
            await resp.Content.ReadAsStreamAsync(), JsonOpts);

        Assert.NotNull(body);
        Assert.Equal("raw", body!.Bucket);
        Assert.Equal(12, body.Buckets.Count);
        Assert.All(body.Buckets, b => Assert.Equal(1, b.SampleCount));
    }

    // -----------------------------------------------------------------
    // Range — invalid bucket / start>end / oversized raw window all return 400.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Range_invalid_bucket_returns_400()
    {
        var client = _factory.CreateClient();
        var url = "/api/readings/range" +
            "?start=2024-01-01T00:00:00Z" +
            "&end=2024-01-02T00:00:00Z" +
            "&bucket=month";
        var resp = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_bucket", raw);
    }

    [Fact]
    public async Task Range_start_after_end_returns_400()
    {
        var client = _factory.CreateClient();
        var url = "/api/readings/range" +
            "?start=2024-01-08T00:00:00Z" +
            "&end=2024-01-01T00:00:00Z" +
            "&bucket=hour";
        var resp = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("start_after_end", raw);
    }

    [Fact]
    public async Task Range_raw_window_too_large_returns_400()
    {
        var client = _factory.CreateClient();
        var url = "/api/readings/range" +
            "?start=2024-01-01T00:00:00Z" +
            "&end=2024-02-15T00:00:00Z" +   // ~45 days; raw cap default 7.
            "&bucket=raw";
        var resp = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("range_too_large", raw);
    }

    // -----------------------------------------------------------------
    // Range — cursor is passed to the fetcher.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Range_passes_cursor_AsOf_to_the_fetcher()
    {
        DateTime? captured = null;
        _factory.RangeFetcher = (bucket, start, end, asOf, ct) =>
        {
            captured = asOf;
            return Task.FromResult<IReadOnlyList<BucketedReading>>(Array.Empty<BucketedReading>());
        };

        var client = _factory.CreateClient();
        var before = DateTime.UtcNow.AddSeconds(-1);
        var url = "/api/readings/range" +
            "?start=2024-01-01T00:00:00Z" +
            "&end=2024-01-01T01:00:00Z" +
            "&bucket=hour";
        await client.GetAsync(url);
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.NotNull(captured);
        Assert.InRange(captured!.Value, before, after);
        Assert.Equal(DateTimeKind.Utc, captured.Value.Kind);
    }

    [Fact]
    public async Task Range_camelCase_field_names_appear_on_the_wire()
    {
        // Inject one row so the JSON contains all the field names.
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _factory.RangeFetcher = (bucket, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<BucketedReading>>(new List<BucketedReading>
            {
                new(t0, 12, 21.0, 20.0, 22.0, 40.0, 38.0, 42.0),
            });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/readings/range?start=2024-01-01T00:00:00Z&end=2024-01-01T01:00:00Z&bucket=hour");
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.Contains("\"bucketTime\"", raw);
        Assert.Contains("\"sampleCount\"", raw);
        Assert.Contains("\"temperatureMean\"", raw);
        Assert.Contains("\"temperatureMin\"", raw);
        Assert.Contains("\"temperatureMax\"", raw);
        Assert.Contains("\"humidityMean\"", raw);
        Assert.Contains("\"humidityMin\"", raw);
        Assert.Contains("\"humidityMax\"", raw);
        Assert.Contains("\"bucket\":\"hour\"", raw);
    }

    // -----------------------------------------------------------------
    // Heatmap — 365 or 366 cells, dense.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Heatmap_returns_366_cells_for_leap_year()
    {
        _factory.HeatmapFetcher = (yearStart, yearEnd, asOf, ct) =>
            Task.FromResult<IReadOnlyList<HeatmapCell>>(new List<HeatmapCell>
            {
                new(new DateOnly(2024, 1, 1),   12, 19.5),
                new(new DateOnly(2024, 6, 15),  24, 23.0),
                new(new DateOnly(2024, 12, 31), 8,  4.5),
            });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/readings/heatmap?year=2024");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await JsonSerializer.DeserializeAsync<WireHeatmapResponse>(
            await resp.Content.ReadAsStreamAsync(), JsonOpts);

        Assert.NotNull(body);
        Assert.Equal(2024, body!.Year);
        Assert.Equal(366, body.Cells.Count);
        Assert.Equal("2024-01-01", body.Cells[0].Date);
        Assert.Equal("2024-12-31", body.Cells[^1].Date);
    }

    [Fact]
    public async Task Heatmap_returns_365_cells_for_common_year()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/readings/heatmap?year=2025");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await JsonSerializer.DeserializeAsync<WireHeatmapResponse>(
            await resp.Content.ReadAsStreamAsync(), JsonOpts);

        Assert.NotNull(body);
        Assert.Equal(365, body!.Cells.Count);
    }

    [Fact]
    public async Task Heatmap_missing_year_returns_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/readings/heatmap");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("missing_year", raw);
    }

    [Fact]
    public async Task Heatmap_invalid_year_returns_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/readings/heatmap?year=1850");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_year", raw);
    }

    // -----------------------------------------------------------------
    // Explorer page renders without errors and references the chart hosts.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Explorer_page_renders_with_chart_hosts_and_dark_theme_assets()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/Explorer");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("explorer-timeseries", html);
        Assert.Contains("explorer-heatmap", html);
        Assert.Contains("data-range=\"1W\"", html);
        Assert.Contains("data-bucket=\"raw\"", html);
        Assert.Contains("plotly-config.js", html);
        Assert.Contains("explorer.js", html);
        // Dark theme background is configured on body
        Assert.Contains("#0d1117", html);
    }

    // -----------------------------------------------------------------
    // Wire DTOs for deserialisation (local to the test class).
    // -----------------------------------------------------------------
    private sealed record WireRangeResponse(
        DateTime Start,
        DateTime End,
        string Bucket,
        List<WireBucketedReading> Buckets);

    private sealed record WireBucketedReading(
        DateTime BucketTime,
        int SampleCount,
        double? TemperatureMean,
        double? TemperatureMin,
        double? TemperatureMax,
        double? HumidityMean,
        double? HumidityMin,
        double? HumidityMax);

    private sealed record WireHeatmapResponse(
        int Year,
        List<WireHeatmapCell> Cells);

    private sealed record WireHeatmapCell(
        string Date,
        int SampleCount,
        double? TemperatureMean);
}
