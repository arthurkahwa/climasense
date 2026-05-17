// SPDX-License-Identifier: MIT
//
// Slice-6 endpoint integration tests for GET /api/leaderboard.
//
// Locks the AC: "`GET /api/leaderboard` returns all rows; the live
// row carries provenance: 'live', others 'notebook'."
//
// The leaderboard SQL is exercised by `LeaderboardReadServiceTests`'s
// pinned-string assertions; this test substitutes a fake fetcher into
// the integration host so the HTTP-level shape (camelCase, nullable
// metrics, ordered rows) is verified end-to-end without touching SQL.

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Leaderboard;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class LeaderboardEndpointTests
    : IClassFixture<LeaderboardEndpointTests.Factory>
{
    private readonly Factory _factory;

    public LeaderboardEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public LeaderboardFetcher Fetcher { get; set; } =
            (ct) => Task.FromResult<IReadOnlyList<LeaderboardRowDto>>(
                Array.Empty<LeaderboardRowDto>());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(LeaderboardReadService))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddScoped<LeaderboardReadService>(_ =>
                    new LeaderboardReadService(
                        (ct) => Fetcher(ct)));
            });
        }
    }

    private sealed record WireRow(
        string ModelName,
        double Mae,
        double Rmse,
        double? Mape,
        double? Smape,
        string Provenance,
        DateTime EvaluatedAt);

    private sealed record WireResponse(List<WireRow> Rows);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task Endpoint_returns_200_with_empty_rows_when_table_empty()
    {
        _factory.Fetcher = (ct) =>
            Task.FromResult<IReadOnlyList<LeaderboardRowDto>>(
                Array.Empty<LeaderboardRowDto>());

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/leaderboard");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<WireResponse>(JsonOpts);
        Assert.NotNull(body);
        Assert.Empty(body!.Rows);
    }

    [Fact]
    public async Task Endpoint_returns_rows_in_camelCase_with_live_provenance()
    {
        var ts = new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc);
        _factory.Fetcher = (ct) =>
            Task.FromResult<IReadOnlyList<LeaderboardRowDto>>(
                new List<LeaderboardRowDto>
                {
                    new("lag-lr-v1", 0.214410, 0.293336, null, null, "live", ts),
                    new("Linear regression (lags)", 0.214410, 0.293336,
                        null, null, "notebook", ts),
                    new("Naive (last value)", 0.217, 0.370, 1.164, 1.153,
                        "notebook", ts),
                });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/leaderboard");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();

        // camelCase on the wire — JSON property names are the lock.
        Assert.Contains("\"rows\"", raw);
        Assert.Contains("\"modelName\"", raw);
        Assert.Contains("\"mae\"", raw);
        Assert.Contains("\"rmse\"", raw);
        Assert.Contains("\"mape\"", raw);
        Assert.Contains("\"smape\"", raw);
        Assert.Contains("\"provenance\"", raw);
        Assert.Contains("\"evaluatedAt\"", raw);

        var body = JsonSerializer.Deserialize<WireResponse>(raw, JsonOpts);
        Assert.NotNull(body);
        Assert.Equal(3, body!.Rows.Count);
        Assert.Equal("live", body.Rows[0].Provenance);
        Assert.Equal("notebook", body.Rows[1].Provenance);
        Assert.Equal("notebook", body.Rows[2].Provenance);
        Assert.Equal(0.214410, body.Rows[0].Mae);
        Assert.Null(body.Rows[0].Mape);
        Assert.Equal(1.164, body.Rows[2].Mape);
    }

    [Fact]
    public async Task Endpoint_response_includes_at_least_one_live_row()
    {
        // AC #4 (issue #8): "the live row carries provenance: 'live',
        // others 'notebook'." This is the structural HTTP-level
        // assertion of that AC — given a response with both
        // provenances, exactly one should be 'live'.
        var ts = new DateTime(2026, 5, 17, 11, 0, 0, DateTimeKind.Utc);
        _factory.Fetcher = (ct) =>
            Task.FromResult<IReadOnlyList<LeaderboardRowDto>>(
                new List<LeaderboardRowDto>
                {
                    new("lag-lr-v1", 0.2144, 0.2933, null, null, "live", ts),
                    new("Naive", 0.217, 0.370, 1.164, 1.153, "notebook", ts),
                    new("Holt-Winters (add, m=24)", 0.247, 0.346, 1.314, 1.310,
                        "notebook", ts),
                });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/leaderboard");
        var body = await resp.Content.ReadFromJsonAsync<WireResponse>(JsonOpts);
        Assert.NotNull(body);
        Assert.Equal(1, body!.Rows.Count(r => r.Provenance == "live"));
        Assert.Equal(2, body.Rows.Count(r => r.Provenance == "notebook"));
    }
}
