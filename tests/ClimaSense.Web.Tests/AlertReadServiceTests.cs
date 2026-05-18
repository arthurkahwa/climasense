// SPDX-License-Identifier: MIT
//
// Slice-11 unit coverage for `AlertReadService` + the golden SQL on
// `SqlAlertReader` (cursor TVF + ORDER BY + TOP (@limit) + join).

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Alerts;
using ClimaSense.Web.Cursor;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class AlertReadServiceTests
{
    private static CursorSnapshot Cursor(DateTime asOf) => new(asOf);

    private static AlertHistoryFetcher Stub(IReadOnlyList<AlertRowDto>? rows = null)
        => (asOf, limit, ct) => Task.FromResult(rows ?? Array.Empty<AlertRowDto>());

    [Fact]
    public void Constructor_rejects_null_fetcher()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AlertReadService(null!));
    }

    [Fact]
    public async Task GetHistory_null_cursor_throws()
    {
        var svc = new AlertReadService(Stub());
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            svc.GetHistoryAsync(null!, 10, CancellationToken.None));
    }

    [Fact]
    public async Task GetHistory_default_limit_when_null()
    {
        int observed = -1;
        var svc = new AlertReadService((asOf, limit, ct) =>
        {
            observed = limit;
            return Task.FromResult<IReadOnlyList<AlertRowDto>>(
                Array.Empty<AlertRowDto>());
        });

        var resp = await svc.GetHistoryAsync(
            Cursor(DateTime.UtcNow), null, CancellationToken.None);

        Assert.Equal(AlertReadService.DefaultLimit, observed);
        Assert.Equal(AlertReadService.DefaultLimit, resp.Limit);
    }

    [Fact]
    public async Task GetHistory_clamps_negative_limit_to_one()
    {
        int observed = -1;
        var svc = new AlertReadService((asOf, limit, ct) =>
        {
            observed = limit;
            return Task.FromResult<IReadOnlyList<AlertRowDto>>(
                Array.Empty<AlertRowDto>());
        });

        await svc.GetHistoryAsync(Cursor(DateTime.UtcNow), -5, CancellationToken.None);
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task GetHistory_clamps_oversize_limit_to_max()
    {
        int observed = -1;
        var svc = new AlertReadService((asOf, limit, ct) =>
        {
            observed = limit;
            return Task.FromResult<IReadOnlyList<AlertRowDto>>(
                Array.Empty<AlertRowDto>());
        });

        await svc.GetHistoryAsync(
            Cursor(DateTime.UtcNow),
            AlertReadService.MaxLimit + 100,
            CancellationToken.None);
        Assert.Equal(AlertReadService.MaxLimit, observed);
    }

    [Fact]
    public async Task GetHistory_passes_cursor_AsOf_to_fetcher()
    {
        var asOf = new DateTime(2026, 5, 17, 14, 30, 0, DateTimeKind.Utc);
        DateTime observed = default;
        var svc = new AlertReadService((a, limit, ct) =>
        {
            observed = a;
            return Task.FromResult<IReadOnlyList<AlertRowDto>>(
                Array.Empty<AlertRowDto>());
        });

        await svc.GetHistoryAsync(Cursor(asOf), 10, CancellationToken.None);

        Assert.Equal(asOf, observed);
    }

    [Fact]
    public async Task GetHistory_count_reflects_fetcher_rowcount()
    {
        var rows = new List<AlertRowDto>
        {
            new(1, 1, "Heat", "T > 26 °C for 30 min",
                new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 17, 10, 0, 0, DateTimeKind.Utc),
                27.5,
                new DateTime(2026, 5, 17, 10, 1, 0, DateTimeKind.Utc)),
            new(2, 3, "Damp", "RH > 70 % for 60 min",
                new DateTime(2026, 5, 17, 4, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 5, 17, 6, 0, 0, DateTimeKind.Utc),
                75.2,
                new DateTime(2026, 5, 17, 6, 1, 0, DateTimeKind.Utc)),
        };
        var svc = new AlertReadService(Stub(rows));

        var resp = await svc.GetHistoryAsync(
            Cursor(DateTime.UtcNow), 50, CancellationToken.None);

        Assert.Equal(2, resp.Count);
        Assert.Equal(2, resp.Rows.Count);
    }

    // -----------------------------------------------------------------
    // Golden-string lock on the SqlAlertReader's history SQL.
    // -----------------------------------------------------------------

    [Fact]
    public void SqlAlertReader_history_sql_uses_cursor_tvf()
    {
        // Cursor-clipping MUST go through the TVF, not an inline
        // WHERE on dbo.Alerts.
        Assert.Contains(
            "dbo.fv_alerts_at_cursor(@asOf)",
            SqlAlertReader.HistorySql);
    }

    [Fact]
    public void SqlAlertReader_history_sql_joins_alert_rules()
    {
        Assert.Contains("dbo.AlertRules", SqlAlertReader.HistorySql);
        Assert.Contains("r.RuleId = a.RuleId", SqlAlertReader.HistorySql);
    }

    [Fact]
    public void SqlAlertReader_history_sql_orders_descending_and_caps()
    {
        Assert.Contains("ORDER BY a.ReplayClockAtFire DESC", SqlAlertReader.HistorySql);
        Assert.Contains("TOP (@limit)", SqlAlertReader.HistorySql);
    }
}
