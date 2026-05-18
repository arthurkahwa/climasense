// SPDX-License-Identifier: MIT
//
// Slice-11 wire-shape DTOs for the alerts surface.
//
// As with the slice-5/6/7/8/9/10 DTOs, we intentionally do NOT reuse
// Kiota-generated POCOs here. Those carry hand-rolled IParsable
// serializers that route around System.Text.Json's
// JsonNamingPolicy.CamelCase configured in Program.cs.
//
// Per the contract (contracts/openapi.yaml §components):
//
//   * AlertRow               — one row in the Alert history table.
//   * AlertHistoryResponse   — paginated wrapper for the history reader.
//   * AlertRuleRow           — one row from `dbo.AlertRules`.
//   * AlertRulesResponse     — wrapper for the rules reader.
//   * BreachDetectedPayload  — the JSON shipped in the SSE
//                              `event: breach-detected` data frame.

#nullable enable

using System;
using System.Collections.Generic;

namespace ClimaSense.Web.Alerts;

/// <summary>
/// One row in the Alert history. Mirrors the `dbo.Alerts` columns plus
/// the joined `dbo.AlertRules.Name` (so the dashboard can render a
/// human-readable rule label without a second hop).
/// </summary>
public sealed record AlertRowDto(
    long AlertId,
    int RuleId,
    string RuleName,
    string RuleSummary,
    DateTime BreachStart,
    DateTime BreachEnd,
    double PeakValue,
    DateTime ReplayClockAtFire);

/// <summary>
/// Paginated wrapper for the history reader. `limit` echoes the value
/// the server actually used (clamped 1..200; default 50). `count` is
/// the number of rows returned in this page.
/// </summary>
public sealed record AlertHistoryResponseDto(
    int Limit,
    int Count,
    IReadOnlyList<AlertRowDto> Rows);

/// <summary>
/// One row from `dbo.AlertRules`. The dashboard uses this to label
/// `RuleId` → human-readable summary when rendering the toast (and as
/// a small "rules in force" inventory).
/// </summary>
public sealed record AlertRuleRowDto(
    int RuleId,
    string Name,
    string Metric,
    string Operator,
    double Threshold,
    int DurationMinutes,
    bool Enabled,
    string Summary);

public sealed record AlertRulesResponseDto(
    IReadOnlyList<AlertRuleRowDto> Rules);

/// <summary>
/// SSE `event: breach-detected` payload — sent once per newly-inserted
/// `dbo.Alerts` row. Fields are in camelCase on the wire.
/// </summary>
public sealed record BreachDetectedPayload(
    long AlertId,
    int RuleId,
    string RuleName,
    string RuleSummary,
    DateTime BreachStart,
    DateTime BreachEnd,
    double PeakValue,
    DateTime ReplayClockAtFire);
