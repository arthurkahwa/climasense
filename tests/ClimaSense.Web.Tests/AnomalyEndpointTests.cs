// SPDX-License-Identifier: MIT
//
// Slice-8 endpoint integration tests for the two web-tier anomaly
// reads: GET /api/anomalies/latest and GET /api/anomalies.
//
// Locks the AC: "Dashboard 'Last anomaly' card updates when the
// cursor crosses a known historical anomaly (visible in the demo
// flow)" — the HTTP wire shape (camelCase, 200/404, range/type
// filter) is verified end-to-end without touching SQL.

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Anomalies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class AnomalyEndpointTests
    : IClassFixture<AnomalyEndpointTests.Factory>
{
    private readonly Factory _factory;

    public AnomalyEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public LatestAnomalyFetcher LatestFetcher { get; set; } =
            (asOf, ct) => Task.FromResult<LatestAnomalyDto?>(null);

        public AnomalyRangeFetcher RangeFetcher { get; set; } =
            (asOf, s, e, t, ct) =>
                Task.FromResult<IReadOnlyList<LatestAnomalyDto>>(
                    Array.Empty<LatestAnomalyDto>());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(AnomalyReadService))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddScoped<AnomalyReadService>(_ =>
                    new AnomalyReadService(
                        (asOf, ct) => LatestFetcher(asOf, ct),
                        (asOf, s, e, t, ct) => RangeFetcher(asOf, s, e, t, ct)));
            });
        }
    }

    [Fact]
    public async Task Latest_returns_404_when_no_anomaly_row()
    {
        _factory.LatestFetcher = (asOf, ct) =>
            Task.FromResult<LatestAnomalyDto?>(null);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/anomalies/latest");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", raw);
        Assert.Contains("no_anomaly_yet", raw);
    }

    [Fact]
    public async Task Latest_returns_200_with_camelCase_row_when_present()
    {
        var reading = new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc);
        var detected = new DateTime(2026, 5, 17, 11, 0, 5, DateTimeKind.Utc);
        _factory.LatestFetcher = (asOf, ct) =>
            Task.FromResult<LatestAnomalyDto?>(
                new LatestAnomalyDto(
                    AnomalyType: "regime_shift",
                    ReadingTime: reading,
                    Severity: 3.42,
                    Description: "PELT changepoint: mean shift 20.0 -> 24.0 °C",
                    DetectedAt: detected));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/anomalies/latest");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"anomalyType\"", raw);
        Assert.Contains("regime_shift", raw);
        Assert.Contains("\"severity\"", raw);
        Assert.Contains("\"readingTime\"", raw);
        Assert.Contains("\"detectedAt\"", raw);
    }

    [Fact]
    public async Task Range_default_window_calls_fetcher_with_defaults()
    {
        var rangeRows = new List<LatestAnomalyDto>
        {
            new LatestAnomalyDto(
                AnomalyType: "sensor_failure",
                ReadingTime: new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc),
                Severity: 1.0,
                Description: "gap 12 min",
                DetectedAt: new DateTime(2026, 5, 17, 11, 0, 5, DateTimeKind.Utc)),
        };
        _factory.RangeFetcher = (asOf, s, e, t, ct) =>
            Task.FromResult<IReadOnlyList<LatestAnomalyDto>>(rangeRows);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/anomalies");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"rows\"", raw);
        Assert.Contains("\"start\"", raw);
        Assert.Contains("\"end\"", raw);
        Assert.Contains("sensor_failure", raw);
    }

    [Fact]
    public async Task Range_rejects_invalid_type_filter()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/anomalies?type=bogus_type");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_type", raw);
    }

    [Fact]
    public async Task Range_rejects_unparseable_start()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/anomalies?start=not-a-date");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_start", raw);
    }

    [Fact]
    public async Task Range_rejects_start_after_end()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/anomalies?start=2026-05-17T12:00:00Z&end=2026-05-17T11:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_range", raw);
    }
}
