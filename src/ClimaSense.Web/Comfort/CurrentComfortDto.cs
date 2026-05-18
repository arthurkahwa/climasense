// SPDX-License-Identifier: MIT
//
// CurrentComfortDto — wire-shape DTO for the web-tier
// `GET /api/comfort/current` read (slice 7).
//
// As with the slice-5 forecast DTOs and the slice-6 leaderboard DTOs,
// we intentionally do NOT reuse the Kiota-generated
// `ComfortScoreResponse` (under `ClimaSense.Web.Generated.MLClient.Models`).
// That Kiota POCO carries hand-rolled `IParsable` serializers that
// round-trip the contract camelCase through their own writers;
// threading them through `System.Text.Json` would skip the global
// `JsonNamingPolicy.CamelCase` configured in `Program.cs`.
//
// Per the contract (contracts/openapi.yaml §components/CurrentComfortResponse):
//   * score        — most recent comfort score (0–100)
//   * rating       — "excellent" / "acceptable" / "marginal" / "uncomfortable"
//   * season       — "summer" / "winter"
//   * bucketTime   — UTC datetime the row was scored for
//   * computedAt   — UTC datetime the row was written into ComfortScores

#nullable enable

using System;

namespace ClimaSense.Web.Comfort;

public sealed record CurrentComfortDto(
    double Score,
    string Rating,
    string Season,
    DateTime BucketTime,
    DateTime ComputedAt);
