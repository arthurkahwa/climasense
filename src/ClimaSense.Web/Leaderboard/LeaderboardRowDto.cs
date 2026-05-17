// SPDX-License-Identifier: MIT
//
// LeaderboardRowDto / LeaderboardResponseDto — wire-shape DTOs for the
// web-tier `GET /api/leaderboard` read (slice 6).
//
// As with the slice-5 forecast DTOs, we intentionally do NOT reuse the
// Kiota-generated `LeaderboardRow` / `LeaderboardResponse` (under
// `ClimaSense.Web.Generated.MLClient.Models`). Those Kiota POCOs carry
// hand-rolled serializers that round-trip the contract camelCase via
// their own writers; threading them through System.Text.Json would
// skip the global `JsonNamingPolicy.CamelCase` configuration. The
// hand-written records below pick up that policy automatically.
//
// Per the contract (contracts/openapi.yaml §components/LeaderboardRow):
//   * modelName    — display name (notebook label or model_version)
//   * mae / rmse   — non-negative floats
//   * mape / smape — nullable floats (sequence_results rows have neither)
//   * provenance   — "notebook" or "live"
//   * evaluatedAt  — UTC datetime the row was persisted

#nullable enable

using System;
using System.Collections.Generic;

namespace ClimaSense.Web.Leaderboard;

public sealed record LeaderboardRowDto(
    string ModelName,
    double Mae,
    double Rmse,
    double? Mape,
    double? Smape,
    string Provenance,
    DateTime EvaluatedAt);

public sealed record LeaderboardResponseDto(
    IReadOnlyList<LeaderboardRowDto> Rows);
