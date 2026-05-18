// SPDX-License-Identifier: MIT
//
// AlertRule — POCO mirror of one row in `dbo.AlertRules`.
//
// The threshold scanner pulls all enabled rules into memory once per
// tick (single-digit row count — these are operator-curated, not
// machine-generated). The POCO is also the unit of input to the
// gaps-and-islands SQL builder in `AlertScanService`.
//
// Provenance of the seeded defaults: PRD #2's "Alerts and SSE"
// section ("e.g. T > 26 °C for 30 min, RH > 70 % for 60 min,
// T < 18 °C for 60 min") — seeded in `scripts/init-db.sql §5` and
// loaded here verbatim.

#nullable enable

using System;

namespace ClimaSense.Web.Alerts;

/// <summary>
/// One AlertRule. Predicate template: a row in `dbo.SensorReadings`
/// satisfies the rule when
/// <c>(Metric == "T" ? Temperature : Humidity) [Operator] Threshold</c>.
/// </summary>
public sealed record AlertRule(
    int RuleId,
    string Name,
    string Metric,
    string Operator,
    double Threshold,
    int DurationMinutes,
    bool Enabled)
{
    /// <summary>
    /// Render a one-line, human-readable summary of the rule, e.g.
    /// <c>"T > 26.000 for 30 min"</c>. Used by the dashboard, the
    /// SSE payload, and the history table.
    /// </summary>
    /// <remarks>
    /// Deliberately invariant-culture so the wire shape is stable
    /// across locales. Threshold is rendered with three decimal
    /// places to mirror the SQL column's <c>DECIMAL(7, 3)</c>
    /// precision; trailing zeros are trimmed for readability.
    /// </remarks>
    public string Summary
    {
        get
        {
            var threshold = Threshold
                .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var unit = Metric switch
            {
                "T" => " °C",
                "RH" => " %",
                _ => string.Empty,
            };
            return $"{Metric} {Operator} {threshold}{unit} for {DurationMinutes} min";
        }
    }

    /// <summary>
    /// SQL column name that the predicate compares against. Whitelisted
    /// to <c>Temperature</c> / <c>Humidity</c> so a future malformed
    /// rule can't be smuggled into the parameterised query.
    /// </summary>
    public string MetricColumn => Metric switch
    {
        "T" => "Temperature",
        "RH" => "Humidity",
        _ => throw new InvalidOperationException(
            $"AlertRule.Metric must be 'T' or 'RH'; got '{Metric}'."),
    };

    /// <summary>
    /// SQL comparison operator. Whitelisted to <c>&gt;</c> / <c>&lt;</c>
    /// so a future malformed rule can't smuggle SQL into the predicate.
    /// </summary>
    public string SqlOperator => Operator switch
    {
        ">" => ">",
        "<" => "<",
        _ => throw new InvalidOperationException(
            $"AlertRule.Operator must be '>' or '<'; got '{Operator}'."),
    };

    /// <summary>
    /// The "peak" aggregate to track for the rule. For a
    /// <c>&gt;</c> threshold we track MAX (the most extreme high);
    /// for <c>&lt;</c> we track MIN (the most extreme low).
    /// </summary>
    public string PeakAggregate => Operator switch
    {
        ">" => "MAX",
        "<" => "MIN",
        _ => throw new InvalidOperationException(
            $"AlertRule.Operator must be '>' or '<'; got '{Operator}'."),
    };
}
