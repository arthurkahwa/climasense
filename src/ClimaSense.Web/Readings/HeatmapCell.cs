// SPDX-License-Identifier: MIT
//
// Wire DTOs for `GET /api/readings/heatmap`. Shape matches the
// `HeatmapCell` + `HeatmapResponse` schemas in
// `contracts/openapi.yaml`. PascalCase properties → camelCase JSON
// via the global `JsonNamingPolicy.CamelCase` in Program.cs.
//
// The `Date` field is serialized as `YYYY-MM-DD` per OpenAPI's
// `format: date`. We use `DateOnly` to keep the in-memory shape free
// of time / timezone ambiguity.

#nullable enable

using System;
using System.Collections.Generic;

namespace ClimaSense.Web.Readings;

/// <summary>
/// One day in the heatmap. Days with no readings (gap in the data)
/// have <see cref="SampleCount"/> = 0 and <see cref="TemperatureMean"/>
/// = <c>null</c> so the dashboard can render the gap uniformly.
/// </summary>
/// <param name="Date">Calendar day (UTC).</param>
/// <param name="SampleCount">Count of readings on this day.</param>
/// <param name="TemperatureMean">
/// Daily mean temperature (°C). <c>null</c> when <see cref="SampleCount"/> is 0.
/// </param>
public sealed record HeatmapCell(
    DateOnly Date,
    int SampleCount,
    double? TemperatureMean);

/// <summary>
/// Heatmap response envelope. <see cref="Cells"/> contains exactly 365
/// or 366 entries — one per calendar day of <see cref="Year"/>.
/// </summary>
public sealed record HeatmapResponse(
    int Year,
    IReadOnlyList<HeatmapCell> Cells);
