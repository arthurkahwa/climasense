// SPDX-License-Identifier: MIT
//
// LatestReading — wire DTO for `GET /api/readings/latest`.
//
// The shape matches the `LatestReading` schema in
// `contracts/openapi.yaml`. Property names are PascalCase here; the
// global `System.Text.Json` `JsonNamingPolicy.CamelCase` configured
// in `Program.cs` produces the camelCase spelling
// (`readingTime`, `temperatureC`, `humidityPct`) on the wire.
//
// Why a hand-written DTO instead of the Kiota-generated
// `Models.LatestReading`:
//
//   * Kiota's generated type derives from `IParsable` and carries
//     framework-specific extension points (backing store, additional
//     data, parsing helpers) that bloat the JSON output of
//     `Results.Json(latest)` unless we customise serialization.
//   * The web tier never *receives* a `LatestReading` from elsewhere —
//     it constructs one from a SQL row. A small record is the cleanest
//     thing to ship.
//   * The Kiota model still exists (slice 2 codegen surface) and is
//     useful for consumers calling the endpoint from .NET; the
//     server-side handler doesn't need it.

#nullable enable

namespace ClimaSense.Web.Readings;

/// <summary>
/// Wire shape for <c>GET /api/readings/latest</c>. PascalCase here →
/// camelCase on the wire (<c>readingTime</c>, <c>temperatureC</c>,
/// <c>humidityPct</c>) via the global <c>JsonNamingPolicy.CamelCase</c>.
/// </summary>
/// <param name="ReadingTime">UTC timestamp of the latest reading.</param>
/// <param name="TemperatureC">Temperature in degrees Celsius.</param>
/// <param name="HumidityPct">Relative humidity as a percentage (0–100).</param>
public sealed record LatestReading(
    DateTime ReadingTime,
    double TemperatureC,
    double HumidityPct);
