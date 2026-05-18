// SPDX-License-Identifier: MIT
//
// DayProfileDto + DayProfilesResponseDto — wire-shape DTOs for the
// slice-9 web-tier read endpoints.
//
// As with the slice-5/6/7/8 DTOs, we intentionally do NOT reuse the
// Kiota-generated DayProfileRow here. That POCO carries hand-rolled
// IParsable serializers that route around System.Text.Json's
// JsonNamingPolicy.CamelCase configured in Program.cs.
//
// Per the contract (contracts/openapi.yaml §components/DayProfileRow):
//   * date            — calendar day (UTC, ISO 8601 YYYY-MM-DD)
//   * dayOfWeek       — 0 = Monday, 6 = Sunday (ISO 8601 weekday)
//   * meanResidual    — mean of standardised residuals for the day
//   * maxAbsZscore    — largest absolute z-score for the day
//   * pattern         — derived label: quiet / warm / cool / volatile

#nullable enable

using System;
using System.Collections.Generic;

namespace ClimaSense.Web.Profiles;

public sealed record DayProfileDto(
    DateOnly Date,
    int DayOfWeek,
    double MeanResidual,
    double MaxAbsZscore,
    string Pattern,
    DateTime ComputedAt);

public sealed record DayProfilesResponseDto(
    DateOnly Start,
    DateOnly End,
    IReadOnlyList<DayProfileDto> Rows);
