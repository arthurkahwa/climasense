// SPDX-License-Identifier: MIT
//
// Slice-11 endpoint integration tests for `GET /api/alerts` and
// `GET /api/alerts/rules`.
//
// Verifies the HTTP wire shape: 200 with camelCase envelopes; 400 on
// invalid `limit`; cursor passthrough into the fetcher. Uses
// `AlertReadService` / `AlertRuleReadService` stubs injected via the
// test factory so no SQL or ml tier is required.
//
// Also strips the `ThresholdAlertScanner` `IHostedService` from DI so
// the test harness doesn't try to hit SQL Server every wall-minute.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Alerts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class AlertEndpointTests
    : IClassFixture<AlertEndpointTests.Factory>
{
    private readonly Factory _factory;

    public AlertEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public AlertHistoryFetcher HistoryFetcher { get; set; } =
            (asOf, limit, ct) =>
                Task.FromResult<IReadOnlyList<AlertRowDto>>(
                    Array.Empty<AlertRowDto>());

        public EnabledRulesFetcher RulesFetcher { get; set; } =
            _ => Task.FromResult<IReadOnlyList<AlertRule>>(
                Array.Empty<AlertRule>());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // Replace AlertReadService.
                foreach (var d in services
                    .Where(d => d.ServiceType == typeof(AlertReadService))
                    .ToList())
                {
                    services.Remove(d);
                }
                services.AddScoped<AlertReadService>(_ =>
                    new AlertReadService(
                        (asOf, limit, ct) => HistoryFetcher(asOf, limit, ct)));

                // Replace AlertRuleReadService.
                foreach (var d in services
                    .Where(d => d.ServiceType == typeof(AlertRuleReadService))
                    .ToList())
                {
                    services.Remove(d);
                }
                services.AddScoped<AlertRuleReadService>(_ =>
                    new AlertRuleReadService(ct => RulesFetcher(ct)));

                // Strip both registrations of the
                // ThresholdAlertScanner so the test host doesn't try
                // to fire SQL scans every wall-minute:
                //
                //   1. The singleton instance registration (used by
                //      Program.cs both as a DI dependency and as the
                //      source for the IHostedService factory).
                //   2. The IHostedService factory registration that
                //      runs the BackgroundService.
                //
                // We don't need a runtime probe — both registrations
                // are unconditionally added by Program.cs and we know
                // their factory shapes. Inspect each ServiceDescriptor
                // for a closed-over `ThresholdAlertScanner` type
                // anywhere in its target metadata.
                foreach (var d in services
                    .Where(d => d.ServiceType == typeof(ThresholdAlertScanner))
                    .ToList())
                {
                    services.Remove(d);
                }
                // For IHostedService, our factory captures the
                // ThresholdAlertScanner type-token; once we've removed
                // its singleton above, the factory call would throw —
                // we simply remove the IHostedService entry whose
                // factory is the canonical pass-through
                // `sp => sp.GetRequiredService<ThresholdAlertScanner>()`.
                // The HeartbeatService is registered via
                // `AddHostedService<HeartbeatService>()` which uses
                // ImplementationType, not ImplementationFactory — easy
                // to distinguish.
                foreach (var d in services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationFactory is not null)
                    .ToList())
                {
                    services.Remove(d);
                }
            });
        }
    }

    // -----------------------------------------------------------------
    // /api/alerts
    // -----------------------------------------------------------------

    [Fact]
    public async Task History_returns_empty_envelope_when_no_alerts()
    {
        _factory.HistoryFetcher = (asOf, limit, ct) =>
            Task.FromResult<IReadOnlyList<AlertRowDto>>(Array.Empty<AlertRowDto>());

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/alerts");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"limit\"", raw);
        Assert.Contains("\"count\":0", raw);
        Assert.Contains("\"rows\":[]", raw);
    }

    [Fact]
    public async Task History_returns_camelCase_rows()
    {
        _factory.HistoryFetcher = (asOf, limit, ct) =>
        {
            var rows = new List<AlertRowDto>
            {
                new(
                    AlertId: 42,
                    RuleId: 1,
                    RuleName: "Heat: T > 26 °C sustained 30 min",
                    RuleSummary: "T > 26 °C for 30 min",
                    BreachStart: new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc),
                    BreachEnd: new DateTime(2026, 5, 17, 10, 0, 0, DateTimeKind.Utc),
                    PeakValue: 27.5,
                    ReplayClockAtFire: new DateTime(2026, 5, 17, 10, 1, 0, DateTimeKind.Utc)),
            };
            return Task.FromResult<IReadOnlyList<AlertRowDto>>(rows);
        };

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/alerts?limit=10");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"alertId\":42", raw);
        Assert.Contains("\"ruleId\":1", raw);
        Assert.Contains("\"ruleName\"", raw);
        Assert.Contains("\"ruleSummary\"", raw);
        Assert.Contains("\"breachStart\"", raw);
        Assert.Contains("\"breachEnd\"", raw);
        Assert.Contains("\"peakValue\":27.5", raw);
        Assert.Contains("\"replayClockAtFire\"", raw);
    }

    [Fact]
    public async Task History_rejects_zero_limit()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/alerts?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_limit", raw);
    }

    [Fact]
    public async Task History_rejects_oversize_limit()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/alerts?limit=99999");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_limit", raw);
    }

    [Fact]
    public async Task History_count_reflects_fetcher_size()
    {
        _factory.HistoryFetcher = (asOf, limit, ct) =>
        {
            var rows = new List<AlertRowDto>
            {
                new(1, 1, "Heat", "T > 26 °C for 30 min",
                    DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1),
                    27.0, DateTime.UtcNow.AddHours(-1)),
                new(2, 3, "Damp", "RH > 70 % for 60 min",
                    DateTime.UtcNow.AddHours(-5), DateTime.UtcNow.AddHours(-3),
                    75.0, DateTime.UtcNow.AddHours(-3)),
            };
            return Task.FromResult<IReadOnlyList<AlertRowDto>>(rows);
        };

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/alerts?limit=50");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"count\":2", raw);
    }

    // -----------------------------------------------------------------
    // /api/alerts/rules
    // -----------------------------------------------------------------

    [Fact]
    public async Task Rules_returns_empty_envelope_when_no_rules()
    {
        _factory.RulesFetcher = _ =>
            Task.FromResult<IReadOnlyList<AlertRule>>(Array.Empty<AlertRule>());

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/alerts/rules");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"rules\":[]", raw);
    }

    [Fact]
    public async Task Rules_returns_camelCase_rows_with_summary()
    {
        _factory.RulesFetcher = _ =>
            Task.FromResult<IReadOnlyList<AlertRule>>(new[]
            {
                new AlertRule(1, "Heat: T > 26 °C sustained 30 min",
                    "T", ">", 26.0, 30, true),
                new AlertRule(3, "Damp: RH > 70 % sustained 60 min",
                    "RH", ">", 70.0, 60, true),
            });

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/alerts/rules");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"ruleId\":1", raw);
        Assert.Contains("\"metric\":\"T\"", raw);
        Assert.Contains("\"operator\":\">\"", raw);
        Assert.Contains("\"durationMinutes\":30", raw);
        Assert.Contains("\"enabled\":true", raw);
        // The summary payload's `>` and `°` will both be Unicode-escaped
        // by System.Text.Json's default JavaScriptEncoder, e.g.
        // `>` and `°`. We assert key+value presence rather
        // than the exact escape form so the test survives a future
        // encoder swap.
        Assert.Contains("\"summary\":", raw);
        Assert.Contains("for 30 min", raw);
    }
}
