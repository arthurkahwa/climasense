// SPDX-License-Identifier: MIT
//
// ComfortBudgetDto + sub-types — wire-shape DTOs for the slice-10
// web-tier `GET /api/comfort/budget` read.
//
// As with the slice-5/6/7/8/9 DTOs, we intentionally do NOT reuse the
// Kiota-generated `ComfortBudgetResponse` POCO. That generator carries
// hand-rolled `IParsable` serializers that route around
// `System.Text.Json.JsonNamingPolicy.CamelCase` configured globally in
// `Program.cs`.
//
// Per the contract (contracts/openapi.yaml §components/ComfortBudgetResponse):
//
//   * hoursOutsideZone — int. Count of `ComfortScores` rows in the
//                        last 7 days with `Score < threshold` (default
//                        threshold = 70, read from
//                        `COMFORT_DISCOMFORT_THRESHOLD`).
//   * threshold        — double. The threshold actually used to evaluate
//                        the count. Surfaced so the dashboard label can
//                        say "(<threshold)" without round-tripping the
//                        config.
//   * windowDays       — int. Width of the window in days. Constant 7
//                        per spec (`COMFORT_BUDGET_WINDOW_DAYS`).
//   * windowStart      — UTC datetime. `cursor - windowDays`.
//   * windowEnd        — UTC datetime. The cursor's `asOf`.
//   * worstCell        — nullable. The `DayProfiles` row in the window
//                        with the most-negative `MeanResidual`. Null
//                        when no `DayProfiles` row is visible.
//   * trend            — array of `ComfortTrendPoint`. One row per
//                        calendar day in the window where at least one
//                        comfort score exists (empty days are omitted).

#nullable enable

using System;
using System.Collections.Generic;

namespace ClimaSense.Web.Comfort;

/// <summary>
/// Single point on the 7-day comfort trend sparkline. One row per
/// calendar day where the comfort scheduler emitted at least one
/// score.
/// </summary>
public sealed record ComfortTrendPointDto(
    DateOnly Day,
    double MinScore,
    double MaxScore,
    double MeanScore,
    int SampleCount);

/// <summary>
/// Worst calendar cell in the window — the <see cref="DayProfileSummary"/>
/// row with the most-negative <c>MeanResidual</c>. Null when no
/// <c>DayProfiles</c> row is visible at or before the cursor.
/// </summary>
public sealed record WorstCalendarCellDto(
    DateOnly Date,
    int DayOfWeek,
    double MeanResidual,
    double MaxAbsZscore,
    string Pattern);

/// <summary>
/// Comfort Budget envelope — all three aggregations in one payload so
/// the dashboard hydrates from a single <c>fetch()</c>.
/// </summary>
public sealed record ComfortBudgetDto(
    int HoursOutsideZone,
    double Threshold,
    int WindowDays,
    DateTime WindowStart,
    DateTime WindowEnd,
    WorstCalendarCellDto? WorstCell,
    IReadOnlyList<ComfortTrendPointDto> Trend);
