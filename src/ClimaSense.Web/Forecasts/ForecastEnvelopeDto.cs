// SPDX-License-Identifier: MIT
//
// ForecastEnvelopeDto / ForecastPointDto — wire-shape DTOs for the
// web-tier `/api/forecasts/latest` read.
//
// We intentionally do NOT reuse the Kiota-generated `ForecastEnvelope`
// (under `ClimaSense.Web.Generated.MLClient.Models`) because that type
// is a Kiota POCO with hand-rolled serializers — round-tripping it
// through System.Text.Json would skip its camelCase contract. The
// hand-written DTOs below are System.Text.Json-friendly and pick up
// the global `JsonNamingPolicy.CamelCase` configured in Program.cs.
//
// Per the contract (contracts/openapi.yaml §components/ForecastEnvelope):
//   * generatedAt   — cursor value at emission
//   * modelVersion  — artefact identifier (e.g. lag-lr-v1)
//   * horizonHours  — count of points in the envelope
//   * points        — list of (targetTime, predictedTemperature, …)

#nullable enable

using System;
using System.Collections.Generic;

namespace ClimaSense.Web.Forecasts;

public sealed record ForecastPointDto(
    DateTime TargetTime,
    double PredictedTemperature,
    double PredictedHumidity,
    double ConfidenceLowerTemp,
    double ConfidenceUpperTemp);

public sealed record ForecastEnvelopeDto(
    DateTime GeneratedAt,
    string ModelVersion,
    int HorizonHours,
    IReadOnlyList<ForecastPointDto> Points);
