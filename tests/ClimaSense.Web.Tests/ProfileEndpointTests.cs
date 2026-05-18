// SPDX-License-Identifier: MIT
//
// Slice-9 endpoint integration tests for `GET /api/profiles`.
//
// Verifies the HTTP wire shape: 200 with camelCase rows; 400 on
// invalid range (start>end, oversized window, unparseable date).
// Uses a `ProfileReadService` stub injected via the test factory so
// no SQL or ml tier is required.

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Profiles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class ProfileEndpointTests
    : IClassFixture<ProfileEndpointTests.Factory>
{
    private readonly Factory _factory;

    public ProfileEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public DayProfileRangeFetcher RangeFetcher { get; set; } =
            (asOf, s, e, ct) =>
                Task.FromResult<IReadOnlyList<DayProfileDto>>(
                    Array.Empty<DayProfileDto>());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(ProfileReadService))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddScoped<ProfileReadService>(_ =>
                    new ProfileReadService(
                        (asOf, s, e, ct) => RangeFetcher(asOf, s, e, ct)));
            });
        }
    }

    [Fact]
    public async Task Range_returns_empty_envelope_with_defaults()
    {
        _factory.RangeFetcher = (asOf, s, e, ct) =>
            Task.FromResult<IReadOnlyList<DayProfileDto>>(
                Array.Empty<DayProfileDto>());

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/profiles");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"start\"", raw);
        Assert.Contains("\"end\"", raw);
        Assert.Contains("\"rows\"", raw);
    }

    [Fact]
    public async Task Range_returns_200_with_camelCase_rows()
    {
        var rows = new List<DayProfileDto>
        {
            new(
                Date: new DateOnly(2026, 5, 17),
                DayOfWeek: 6,
                MeanResidual: 0.012,
                MaxAbsZscore: 3.14,
                Pattern: "volatile",
                ComputedAt: new DateTime(2026, 5, 17, 4, 0, 0, DateTimeKind.Utc)),
            new(
                Date: new DateOnly(2026, 5, 16),
                DayOfWeek: 5,
                MeanResidual: -0.04,
                MaxAbsZscore: 1.0,
                Pattern: "cool",
                ComputedAt: new DateTime(2026, 5, 16, 4, 0, 0, DateTimeKind.Utc)),
        };
        _factory.RangeFetcher = (asOf, s, e, ct) =>
            Task.FromResult<IReadOnlyList<DayProfileDto>>(rows);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/profiles?start=2026-05-10&end=2026-05-17");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"dayOfWeek\"", raw);
        Assert.Contains("\"meanResidual\"", raw);
        Assert.Contains("\"maxAbsZscore\"", raw);
        Assert.Contains("\"pattern\"", raw);
        Assert.Contains("\"computedAt\"", raw);
        Assert.Contains("volatile", raw);
        Assert.Contains("cool", raw);
    }

    [Fact]
    public async Task Range_rejects_unparseable_start()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/profiles?start=not-a-date");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_start", raw);
    }

    [Fact]
    public async Task Range_rejects_unparseable_end()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/profiles?end=2026-13-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_end", raw);
    }

    [Fact]
    public async Task Range_rejects_start_after_end()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/profiles?start=2026-05-17&end=2026-05-16");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_range", raw);
    }

    [Fact]
    public async Task Range_rejects_oversized_window()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/profiles?start=2020-01-01&end=2025-01-01");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_range", raw);
    }
}
