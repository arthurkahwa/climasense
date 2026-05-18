// SPDX-License-Identifier: MIT
//
// Slice-11 unit coverage for `AlertRuleReadService` and the rules
// loader's SQL shape.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Alerts;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class AlertRuleReadServiceTests
{
    [Fact]
    public void Constructor_rejects_null_fetcher()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AlertRuleReadService(null!));
    }

    [Fact]
    public async Task GetAll_projects_each_AlertRule_into_a_row()
    {
        var rules = new List<AlertRule>
        {
            new(1, "Heat", "T",  ">", 26.0, 30, true),
            new(2, "Cold", "T",  "<", 18.0, 60, true),
            new(3, "Damp", "RH", ">", 70.0, 60, true),
        };
        var svc = new AlertRuleReadService(_ => Task.FromResult<IReadOnlyList<AlertRule>>(rules));

        var resp = await svc.GetAllAsync(CancellationToken.None);

        Assert.Equal(3, resp.Rules.Count);
        Assert.Equal("Heat", resp.Rules[0].Name);
        // Summary is rendered from the rule's predicate, not pulled from
        // a separate SQL column — locks the "one source of truth" claim.
        Assert.Equal("T > 26 °C for 30 min", resp.Rules[0].Summary);
        Assert.Equal("T < 18 °C for 60 min", resp.Rules[1].Summary);
        Assert.Equal("RH > 70 % for 60 min", resp.Rules[2].Summary);
    }

    [Fact]
    public async Task GetAll_empty_input_produces_empty_envelope()
    {
        var svc = new AlertRuleReadService(_ =>
            Task.FromResult<IReadOnlyList<AlertRule>>(Array.Empty<AlertRule>()));

        var resp = await svc.GetAllAsync(CancellationToken.None);
        Assert.Empty(resp.Rules);
    }

    // -----------------------------------------------------------------
    // Golden-string locks on the SqlAlertScanner rules loader.
    // -----------------------------------------------------------------

    [Fact]
    public void SqlAlertScanner_load_rules_sql_filters_enabled_only()
    {
        Assert.Contains("WHERE Enabled = 1", SqlAlertScanner.LoadRulesSql);
    }

    [Fact]
    public void SqlAlertScanner_load_rules_sql_orders_by_RuleId()
    {
        Assert.Contains("ORDER BY RuleId ASC", SqlAlertScanner.LoadRulesSql);
    }

    [Fact]
    public void SqlAlertScanner_insert_sql_guards_against_double_insertion()
    {
        // The WHERE NOT EXISTS clause is what makes re-detection a
        // silent no-op — pin its shape.
        Assert.Contains("WHERE NOT EXISTS", SqlAlertScanner.InsertAlertSql);
        Assert.Contains("RuleId = @ruleId", SqlAlertScanner.InsertAlertSql);
        Assert.Contains("BreachStart = @breachStart", SqlAlertScanner.InsertAlertSql);
    }

    [Fact]
    public void SqlAlertScanner_insert_sql_returns_alert_id_via_output_table()
    {
        // OUTPUT INSERTED.AlertId INTO @out → SELECT FROM @out is how
        // the inserter distinguishes "row inserted" vs "row already
        // present" without trapping a duplicate-key exception.
        Assert.Contains("OUTPUT INSERTED.AlertId INTO @out", SqlAlertScanner.InsertAlertSql);
        Assert.Contains("SELECT AlertId FROM @out", SqlAlertScanner.InsertAlertSql);
    }
}
