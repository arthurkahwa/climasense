// SPDX-License-Identifier: MIT
//
// Slice-10 endpoint integration tests for `GET /api/comfort/budget`.
//
// Verifies the HTTP wire shape: 200 with camelCase envelope; empty
// state (zero hours / null worstCell / empty trend) handled as 200
// not 404; cursor passthrough into the fetcher; threshold/window
// reflected in the response so the dashboard label can render
// without round-tripping to config.
//
// Uses a `ComfortBudgetReadService` stub injected via the test
// factory so no SQL or ml tier is required.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Comfort;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class ComfortBudgetEndpointTests
    : IClassFixture<ComfortBudgetEndpointTests.Factory>
{
    private readonly Factory _factory;

    public ComfortBudgetEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public ComfortBudgetFetcher Fetcher { get; set; } =
            (a, s, e, w, t, ct) => Task.FromResult(new ComfortBudgetDto(
                HoursOutsideZone: 0,
                Threshold: t,
                WindowDays: w,
                WindowStart: s,
                WindowEnd: e,
                WorstCell: null,
                Trend: Array.Empty<ComfortTrendPointDto>()));

        public double Threshold { get; set; } = 70.0;
        public int WindowDays { get; set; } = 7;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(ComfortBudgetReadService))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddScoped<ComfortBudgetReadService>(_ =>
                    new ComfortBudgetReadService(
                        (a, s, e, w, t, ct) => Fetcher(a, s, e, w, t, ct),
                        threshold: Threshold,
                        windowDays: WindowDays));
            });
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task Endpoint_returns_200_with_empty_budget_when_tables_empty()
    {
        // Empty tables: zero hours, null worst cell, empty trend.
        // Documented behaviour — the dashboard handles this without
        // an error state.
        _factory.Fetcher = (a, s, e, w, t, ct) =>
            Task.FromResult(new ComfortBudgetDto(
                HoursOutsideZone: 0,
                Threshold: t,
                WindowDays: w,
                WindowStart: s,
                WindowEnd: e,
                WorstCell: null,
                Trend: Array.Empty<ComfortTrendPointDto>()));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/comfort/budget");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"hoursOutsideZone\"", raw);
        Assert.Contains("\"threshold\"", raw);
        Assert.Contains("\"windowDays\"", raw);
        Assert.Contains("\"windowStart\"", raw);
        Assert.Contains("\"windowEnd\"", raw);
        Assert.Contains("\"worstCell\"", raw);
        Assert.Contains("\"trend\"", raw);
        Assert.Contains("\"hoursOutsideZone\":0", raw);
    }

    [Fact]
    public async Task Endpoint_returns_200_with_camelCase_envelope_when_present()
    {
        var asOf = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
        var worst = new WorstCalendarCellDto(
            Date: new DateOnly(2026, 5, 14),
            DayOfWeek: 3,
            MeanResidual: -1.83,
            MaxAbsZscore: 3.10,
            Pattern: "cool");
        var trend = new List<ComfortTrendPointDto>
        {
            new(new DateOnly(2026, 5, 15), 60.0, 95.0, 81.2, 24),
            new(new DateOnly(2026, 5, 16), 55.0, 90.0, 75.0, 24),
            new(new DateOnly(2026, 5, 17), 50.0, 88.0, 70.5, 12),
        };
        _factory.Fetcher = (a, s, e, w, t, ct) =>
            Task.FromResult(new ComfortBudgetDto(
                HoursOutsideZone: 17,
                Threshold: t,
                WindowDays: w,
                WindowStart: s,
                WindowEnd: e,
                WorstCell: worst,
                Trend: trend));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/comfort/budget");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();

        // CamelCase on the wire.
        Assert.Contains("\"hoursOutsideZone\":17", raw);
        Assert.Contains("\"meanResidual\":-1.83", raw);
        Assert.Contains("\"maxAbsZscore\":3.1", raw);
        Assert.Contains("\"pattern\":\"cool\"", raw);
        Assert.Contains("\"dayOfWeek\":3", raw);
        Assert.Contains("\"meanScore\"", raw);
        Assert.Contains("\"minScore\"", raw);
        Assert.Contains("\"maxScore\"", raw);
        Assert.Contains("\"sampleCount\"", raw);
    }

    [Fact]
    public async Task Endpoint_reports_configured_threshold_and_window()
    {
        _factory.Threshold = 80.0;
        _factory.WindowDays = 7;
        _factory.Fetcher = (a, s, e, w, t, ct) =>
            Task.FromResult(new ComfortBudgetDto(
                HoursOutsideZone: 0,
                Threshold: t,
                WindowDays: w,
                WindowStart: s,
                WindowEnd: e,
                WorstCell: null,
                Trend: Array.Empty<ComfortTrendPointDto>()));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/comfort/budget");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"threshold\":80", raw);
        Assert.Contains("\"windowDays\":7", raw);

        // Reset for other tests in the same class.
        _factory.Threshold = 70.0;
    }

    [Fact]
    public async Task Endpoint_passes_cursor_value_to_fetcher_as_windowEnd()
    {
        // Locks the cursor passthrough — the endpoint computes
        // `[cursor - 7d, cursor]` and passes both bounds plus the
        // cursor itself to the fetcher. The response's `windowEnd`
        // is the cursor.
        DateTime? capturedAsOf = null;
        DateTime? capturedStart = null;
        DateTime? capturedEnd = null;
        _factory.Fetcher = (a, s, e, w, t, ct) =>
        {
            capturedAsOf = a;
            capturedStart = s;
            capturedEnd = e;
            return Task.FromResult(new ComfortBudgetDto(
                HoursOutsideZone: 0,
                Threshold: t,
                WindowDays: w,
                WindowStart: s,
                WindowEnd: e,
                WorstCell: null,
                Trend: Array.Empty<ComfortTrendPointDto>()));
        };

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/comfort/budget");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(capturedAsOf);
        Assert.NotNull(capturedStart);
        Assert.NotNull(capturedEnd);
        // The end == as_of (cursor), and start == as_of - 7 days.
        Assert.Equal(capturedAsOf!.Value, capturedEnd!.Value);
        Assert.Equal(
            capturedAsOf.Value.AddDays(-7),
            capturedStart!.Value);
    }

    [Fact]
    public async Task Endpoint_serializes_worstCell_as_null_when_absent()
    {
        // Distinct from "missing key" — the dashboard distinguishes
        // null (no worst cell visible) from undefined (response shape
        // changed).
        _factory.Fetcher = (a, s, e, w, t, ct) =>
            Task.FromResult(new ComfortBudgetDto(
                HoursOutsideZone: 0,
                Threshold: t,
                WindowDays: w,
                WindowStart: s,
                WindowEnd: e,
                WorstCell: null,
                Trend: Array.Empty<ComfortTrendPointDto>()));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/comfort/budget");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"worstCell\":null", raw);
    }

    [Fact]
    public async Task Endpoint_returns_consistent_body_on_repeat()
    {
        // Replay safety — two consecutive reads with a stable fetcher
        // produce identical bodies. Locks the "no per-request mutation"
        // property of the read service.
        var asOf = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
        _factory.Fetcher = (a, s, e, w, t, ct) =>
            Task.FromResult(new ComfortBudgetDto(
                HoursOutsideZone: 4,
                Threshold: t,
                WindowDays: w,
                WindowStart: s,
                WindowEnd: e,
                WorstCell: null,
                Trend: Array.Empty<ComfortTrendPointDto>()));

        var client = _factory.CreateClient();
        var first = await client.GetAsync("/api/comfort/budget");
        var second = await client.GetAsync("/api/comfort/budget");
        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();

        // Bodies differ only by the response framing's wall-clock
        // cursor (under WallClock, AsOf moves between reads). We
        // compare just the core counts to keep the assertion stable.
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("\"hoursOutsideZone\":4", firstBody);
        Assert.Contains("\"hoursOutsideZone\":4", secondBody);
    }
}
