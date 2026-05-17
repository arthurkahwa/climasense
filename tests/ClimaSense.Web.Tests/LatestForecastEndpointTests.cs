// SPDX-License-Identifier: MIT
//
// Slice-5 endpoint integration tests for GET /api/forecasts/latest.
//
// Locks the AC: "GET /api/forecasts/latest reads through
// dbo.fv_forecasts_at_cursor(@asOf); older forecasts disappear from
// the response when the cursor seeks back past them."
//
// The TVF is a SQL-level concern, so the unit-style test pins:
//   * Empty case → 200 with `points: []` and `horizonHours: 0`.
//   * Populated case → 200 with `generatedAt`, `modelVersion`,
//     `horizonHours`, and `points[]` in camelCase.
//   * The cursor's `AsOf` is passed verbatim to the fetcher (cursor-
//     clipping happens inside the fetcher's SQL via the TVF).
//
// The "older forecasts disappear" SQL-level claim is unit-tested by
// the `SqlForecastFetcher_query_goes_through_inline_TVF` assertion in
// `ForecastReadServiceTests.cs` plus the bound integration check in
// the docker-compose lifecycle (see PR body).

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Forecasts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class LatestForecastEndpointTests
    : IClassFixture<LatestForecastEndpointTests.Factory>
{
    private readonly Factory _factory;

    public LatestForecastEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public LatestForecastBatchFetcher Fetcher { get; set; } =
            (asOf, ct) => Task.FromResult<(DateTime?, string?, IReadOnlyList<ForecastPointDto>)>(
                (null, null, Array.Empty<ForecastPointDto>()));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(ForecastReadService))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddScoped<ForecastReadService>(_ =>
                    new ForecastReadService(
                        (asOf, ct) => Fetcher(asOf, ct)));
            });
        }
    }

    private sealed record WirePoint(
        DateTime TargetTime,
        double PredictedTemperature,
        double PredictedHumidity,
        double ConfidenceLowerTemp,
        double ConfidenceUpperTemp);

    private sealed record WireEnvelope(
        DateTime GeneratedAt,
        string ModelVersion,
        int HorizonHours,
        List<WirePoint> Points);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task Endpoint_returns_200_with_empty_envelope_when_no_rows()
    {
        _factory.Fetcher = (asOf, ct) =>
            Task.FromResult<(DateTime?, string?, IReadOnlyList<ForecastPointDto>)>(
                (null, "lag-lr-v1", Array.Empty<ForecastPointDto>()));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/forecasts/latest");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WireEnvelope>(JsonOpts);
        Assert.NotNull(body);
        Assert.Equal(0, body!.HorizonHours);
        Assert.Empty(body.Points);
        Assert.Equal("lag-lr-v1", body.ModelVersion);
    }

    [Fact]
    public async Task Endpoint_returns_envelope_with_points_in_camelCase()
    {
        var generated = new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc);
        var target = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
        _factory.Fetcher = (asOf, ct) =>
            Task.FromResult<(DateTime?, string?, IReadOnlyList<ForecastPointDto>)>((
                generated,
                "lag-lr-v1",
                new List<ForecastPointDto>
                {
                    new(target, 21.5, 47.0, 20.9, 22.1),
                    new(target.AddHours(1), 21.6, 47.1, 21.0, 22.2),
                }));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/forecasts/latest");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();

        // camelCase on the wire.
        Assert.Contains("\"generatedAt\"", raw);
        Assert.Contains("\"modelVersion\"", raw);
        Assert.Contains("\"horizonHours\"", raw);
        Assert.Contains("\"points\"", raw);
        Assert.Contains("\"targetTime\"", raw);
        Assert.Contains("\"predictedTemperature\"", raw);
        Assert.Contains("\"confidenceLowerTemp\"", raw);

        var body = JsonSerializer.Deserialize<WireEnvelope>(raw, JsonOpts);
        Assert.NotNull(body);
        Assert.Equal(2, body!.HorizonHours);
        Assert.Equal(2, body.Points.Count);
        Assert.Equal(21.5, body.Points[0].PredictedTemperature);
        Assert.Equal(22.1, body.Points[0].ConfidenceUpperTemp);
    }

    [Fact]
    public async Task Endpoint_passes_cursor_AsOf_to_the_fetcher()
    {
        DateTime? captured = null;
        _factory.Fetcher = (asOf, ct) =>
        {
            captured = asOf;
            return Task.FromResult<(DateTime?, string?, IReadOnlyList<ForecastPointDto>)>(
                (null, null, Array.Empty<ForecastPointDto>()));
        };

        var client = _factory.CreateClient();
        var before = DateTime.UtcNow.AddSeconds(-1);
        var resp = await client.GetAsync("/api/forecasts/latest");
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(captured);
        Assert.InRange(captured!.Value, before, after);
        Assert.Equal(DateTimeKind.Utc, captured.Value.Kind);
    }
}
