// SPDX-License-Identifier: MIT
//
// Slice-7 endpoint integration tests for GET /api/comfort/current.
//
// Locks the AC: "Dashboard's comfort card shows score, rating label,
// and season label; reloading shows the same values."
//
// The SQL is exercised by `ComfortReadServiceTests`'s pinned-string
// assertions; this test substitutes a fake fetcher into the integration
// host so the HTTP-level shape (camelCase, 200/404 semantics) is
// verified end-to-end without touching SQL.

#nullable enable

using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Comfort;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class ComfortEndpointTests
    : IClassFixture<ComfortEndpointTests.Factory>
{
    private readonly Factory _factory;

    public ComfortEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public CurrentComfortFetcher Fetcher { get; set; } =
            (asOf, ct) => Task.FromResult<CurrentComfortDto?>(null);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(ComfortReadService))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddScoped<ComfortReadService>(_ =>
                    new ComfortReadService(
                        (asOf, ct) => Fetcher(asOf, ct)));
            });
        }
    }

    private sealed record WireComfort(
        double Score,
        string Rating,
        string Season,
        DateTime BucketTime,
        DateTime ComputedAt);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task Endpoint_returns_404_when_no_comfort_row()
    {
        _factory.Fetcher = (asOf, ct) =>
            Task.FromResult<CurrentComfortDto?>(null);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/comfort/current");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", raw);
        Assert.Contains("no_comfort_yet", raw);
    }

    [Fact]
    public async Task Endpoint_returns_200_with_camelCase_row_when_present()
    {
        var bucket = new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc);
        var computedAt = new DateTime(2026, 5, 17, 11, 0, 5, DateTimeKind.Utc);
        _factory.Fetcher = (asOf, ct) =>
            Task.FromResult<CurrentComfortDto?>(
                new CurrentComfortDto(
                    Score: 92.5,
                    Rating: "excellent",
                    Season: "summer",
                    BucketTime: bucket,
                    ComputedAt: computedAt));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/comfort/current");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();

        // camelCase on the wire — JSON property names are the lock.
        Assert.Contains("\"score\"", raw);
        Assert.Contains("\"rating\"", raw);
        Assert.Contains("\"season\"", raw);
        Assert.Contains("\"bucketTime\"", raw);
        Assert.Contains("\"computedAt\"", raw);

        var body = JsonSerializer.Deserialize<WireComfort>(raw, JsonOpts);
        Assert.NotNull(body);
        Assert.Equal(92.5, body!.Score);
        Assert.Equal("excellent", body.Rating);
        Assert.Equal("summer", body.Season);
        Assert.Equal(bucket, body.BucketTime);
        Assert.Equal(computedAt, body.ComputedAt);
    }

    [Fact]
    public async Task Endpoint_reloading_returns_same_values_when_fetcher_stable()
    {
        // AC: "reloading shows the same values" — the endpoint is a
        // pure read; two consecutive GETs against the same row must
        // return the same JSON body.
        var bucket = new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc);
        var row = new CurrentComfortDto(
            Score: 78.0,
            Rating: "acceptable",
            Season: "winter",
            BucketTime: bucket,
            ComputedAt: bucket.AddSeconds(2));
        _factory.Fetcher = (asOf, ct) =>
            Task.FromResult<CurrentComfortDto?>(row);

        var client = _factory.CreateClient();
        var first = await client.GetAsync("/api/comfort/current");
        var second = await client.GetAsync("/api/comfort/current");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.Equal(firstBody, secondBody);
    }
}
