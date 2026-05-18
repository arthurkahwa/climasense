// SPDX-License-Identifier: MIT
//
// LatestAnomalyDto + AnomaliesResponseDto — wire-shape DTOs for the
// slice-8 web-tier anomaly reads.
//
// As with slice 5/6/7 DTOs, we intentionally do NOT reuse the
// Kiota-generated `AnomalyRow` here. That POCO carries hand-rolled
// `IParsable` serializers that round-trip the contract camelCase
// through their own writers; threading them through
// `System.Text.Json` would skip the global
// `JsonNamingPolicy.CamelCase` configured in `Program.cs`.
//
// Per the contract (contracts/openapi.yaml §components/LatestAnomalyResponse):
//   * anomalyType  — "sensor_failure" / "regime_shift" / "residual_outlier"
//   * readingTime  — UTC datetime the row was flagged for
//   * severity     — |residual| / σ for residual_outlier; 1.0 otherwise
//   * description  — optional short free-text rationale
//   * detectedAt   — UTC datetime the detector wrote the row

#nullable enable

using System;
using System.Collections.Generic;

namespace ClimaSense.Web.Anomalies;

public sealed record LatestAnomalyDto(
    string AnomalyType,
    DateTime ReadingTime,
    double Severity,
    string? Description,
    DateTime DetectedAt);

public sealed record AnomaliesResponseDto(
    DateTime Start,
    DateTime End,
    string? Type,
    IReadOnlyList<LatestAnomalyDto> Rows);
