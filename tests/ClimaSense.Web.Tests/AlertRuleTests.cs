// SPDX-License-Identifier: MIT
//
// Slice-11 unit coverage for the `AlertRule` POCO:
//
//   * `Summary` renders the canonical "Metric Op Threshold ... for N min"
//     shape across all three seeded rules.
//   * `MetricColumn` / `SqlOperator` / `PeakAggregate` whitelist refusals
//     keep caller-controlled strings out of the rendered SQL.

#nullable enable

using System;
using ClimaSense.Web.Alerts;
using Xunit;

namespace ClimaSense.Web.Tests;

public sealed class AlertRuleTests
{
    [Fact]
    public void Summary_renders_heat_rule_correctly()
    {
        var rule = new AlertRule(
            RuleId: 1,
            Name: "Heat",
            Metric: "T",
            Operator: ">",
            Threshold: 26.0,
            DurationMinutes: 30,
            Enabled: true);
        Assert.Equal("T > 26 °C for 30 min", rule.Summary);
    }

    [Fact]
    public void Summary_renders_cold_rule_correctly()
    {
        var rule = new AlertRule(
            RuleId: 2,
            Name: "Cold",
            Metric: "T",
            Operator: "<",
            Threshold: 18.0,
            DurationMinutes: 60,
            Enabled: true);
        Assert.Equal("T < 18 °C for 60 min", rule.Summary);
    }

    [Fact]
    public void Summary_renders_damp_rule_correctly()
    {
        var rule = new AlertRule(
            RuleId: 3,
            Name: "Damp",
            Metric: "RH",
            Operator: ">",
            Threshold: 70.0,
            DurationMinutes: 60,
            Enabled: true);
        Assert.Equal("RH > 70 % for 60 min", rule.Summary);
    }

    [Fact]
    public void Summary_keeps_fractional_threshold()
    {
        var rule = new AlertRule(
            RuleId: 99,
            Name: "Half-degree",
            Metric: "T",
            Operator: ">",
            Threshold: 26.5,
            DurationMinutes: 15,
            Enabled: true);
        Assert.Contains("26.5", rule.Summary);
    }

    [Fact]
    public void MetricColumn_maps_T_to_Temperature()
    {
        var rule = NewRule(metric: "T");
        Assert.Equal("Temperature", rule.MetricColumn);
    }

    [Fact]
    public void MetricColumn_maps_RH_to_Humidity()
    {
        var rule = NewRule(metric: "RH");
        Assert.Equal("Humidity", rule.MetricColumn);
    }

    [Fact]
    public void MetricColumn_rejects_unknown_metric()
    {
        var rule = NewRule(metric: "pH");
        Assert.Throws<InvalidOperationException>(() => _ = rule.MetricColumn);
    }

    [Theory]
    [InlineData(">", ">")]
    [InlineData("<", "<")]
    public void SqlOperator_passes_allowed_operators_through(
        string given, string expected)
    {
        var rule = NewRule(op: given);
        Assert.Equal(expected, rule.SqlOperator);
    }

    [Theory]
    [InlineData("=")]
    [InlineData(">=")]
    [InlineData("OR 1=1")]
    public void SqlOperator_rejects_disallowed_operators(string op)
    {
        var rule = NewRule(op: op);
        Assert.Throws<InvalidOperationException>(() => _ = rule.SqlOperator);
    }

    [Fact]
    public void PeakAggregate_uses_MAX_for_gt_rules()
    {
        var rule = NewRule(op: ">");
        Assert.Equal("MAX", rule.PeakAggregate);
    }

    [Fact]
    public void PeakAggregate_uses_MIN_for_lt_rules()
    {
        var rule = NewRule(op: "<");
        Assert.Equal("MIN", rule.PeakAggregate);
    }

    private static AlertRule NewRule(string metric = "T", string op = ">")
        => new(
            RuleId: 1,
            Name: "Test",
            Metric: metric,
            Operator: op,
            Threshold: 26.0,
            DurationMinutes: 30,
            Enabled: true);
}
