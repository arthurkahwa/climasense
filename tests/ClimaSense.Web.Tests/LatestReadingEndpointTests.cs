// SPDX-License-Identifier: MIT
//
// Slice-3 endpoint integration tests for GET /api/readings/latest.
//
// Locks the AC: "curl http://127.0.0.1:8080/api/readings/latest returns
// a JSON object with readingTime, temperatureC, humidityPct (camelCase)."
//
// Strategy:
//   * `WebApplicationFactory<Program>` spins up the real pipeline in
//     memory.
//   * `ConfigureServices` replaces the production `SensorDataService`
//     with one wired to a lambda fetcher — we never need a live SQL
//     Server. The cursor's AsOf still routes through the real
//     CursorSnapshot scoped binding.
//   * The 404 path is covered with a fetcher returning null —
//     mirrors the "bootstrap incomplete" condition.

#nullable enable

using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Cursor;
using ClimaSense.Web.Readings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class LatestReadingEndpointTests
    : IClassFixture<LatestReadingEndpointTests.Factory>
{
    private readonly Factory _factory;

    public LatestReadingEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------
    // Custom WebApplicationFactory: replaces SensorDataService with a
    // configurable fetcher per test.
    // -----------------------------------------------------------------
    public sealed class Factory : WebApplicationFactory<Program>
    {
        /// <summary>
        /// Mutable per-test fetcher. Default returns a canned row near
        /// 2026-05-07 (the latest reading in the real CSV).
        /// </summary>
        public LatestReadingFetcher Fetcher { get; set; } =
            (asOf, ct) => Task.FromResult<LatestReading?>(
                new LatestReading(
                    ReadingTime: new DateTime(2026, 5, 7, 23, 59, 0, DateTimeKind.Utc),
                    TemperatureC: 22.125,
                    HumidityPct: 48.500));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // Drop the production-wired SensorDataService and rebind
                // with a delegate that captures the per-test fetcher.
                var existing = services
                    .Where(d => d.ServiceType == typeof(SensorDataService))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddScoped<SensorDataService>(_ =>
                    new SensorDataService(
                        (asOf, ct) => Fetcher(asOf, ct)));
            });
        }
    }

    private sealed record WireLatestReading(
        DateTime ReadingTime,
        double TemperatureC,
        double HumidityPct);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // -----------------------------------------------------------------
    // Happy path.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Endpoint_returns_200_with_camelCase_body()
    {
        var fixedReading = new LatestReading(
            ReadingTime: new DateTime(2026, 5, 7, 23, 59, 0, DateTimeKind.Utc),
            TemperatureC: 22.125,
            HumidityPct: 48.500);
        _factory.Fetcher = (asOf, ct) => Task.FromResult<LatestReading?>(fixedReading);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/readings/latest");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();

        // camelCase on the wire — assert the spelling appears in the
        // serialized response.
        Assert.Contains("\"readingTime\"", raw);
        Assert.Contains("\"temperatureC\"", raw);
        Assert.Contains("\"humidityPct\"", raw);

        var body = JsonSerializer.Deserialize<WireLatestReading>(raw, JsonOpts);
        Assert.NotNull(body);
        Assert.Equal(22.125, body!.TemperatureC);
        Assert.Equal(48.500, body.HumidityPct);
    }

    [Fact]
    public async Task Endpoint_round_trips_a_realistic_reading_shape()
    {
        // Last row of sensor_data.csv is around 2026-05-07. Use a value
        // close to that to validate the dashboard's "near 2026-05-07"
        // sanity check from the slice-3 brief.
        var fixedReading = new LatestReading(
            ReadingTime: new DateTime(2026, 5, 7, 16, 17, 0, DateTimeKind.Utc),
            TemperatureC: 21.000,
            HumidityPct: 53.000);
        _factory.Fetcher = (asOf, ct) => Task.FromResult<LatestReading?>(fixedReading);

        var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<WireLatestReading>("/api/readings/latest", JsonOpts);

        Assert.NotNull(body);
        Assert.Equal(new DateTime(2026, 5, 7, 16, 17, 0, DateTimeKind.Utc), body!.ReadingTime.ToUniversalTime());
        Assert.Equal(21.0, body.TemperatureC);
        Assert.Equal(53.0, body.HumidityPct);
    }

    // -----------------------------------------------------------------
    // Empty table -> 404 with no_readings_yet.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Endpoint_returns_404_when_table_empty()
    {
        _factory.Fetcher = (asOf, ct) => Task.FromResult<LatestReading?>(null);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/readings/latest");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":\"no_readings_yet\"", raw);
        Assert.Contains("bootstrap", raw, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------
    // Cursor wiring: the cursor's AsOf is what gets passed to the fetcher.
    // -----------------------------------------------------------------
    [Fact]
    public async Task Endpoint_passes_cursor_AsOf_to_the_fetcher()
    {
        DateTime? captured = null;
        _factory.Fetcher = (asOf, ct) =>
        {
            captured = asOf;
            return Task.FromResult<LatestReading?>(
                new LatestReading(
                    ReadingTime: asOf.AddMinutes(-1),
                    TemperatureC: 20,
                    HumidityPct: 40));
        };

        var client = _factory.CreateClient();
        var before = DateTime.UtcNow.AddSeconds(-1);
        var resp = await client.GetAsync("/api/readings/latest");
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(captured);
        Assert.InRange(captured!.Value, before, after);
        Assert.Equal(DateTimeKind.Utc, captured.Value.Kind);
    }
}
