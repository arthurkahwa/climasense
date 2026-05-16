// SPDX-License-Identifier: MIT
//
// IMLServiceClient — hand-written interface (per issue #4 spec).
//
// Why a hand-written interface (vs Kiota-only):
//   * The contract has two real adapters: `MLServiceClient` (HTTP) and
//     `FakeMLServiceClient` (in-memory, used by `MLServiceClientTests`).
//     Per ADR-0011 ("interface emergence policy: two adapters = real
//     seam"), the seam exists, so the interface ships in slice 2.
//   * The hand-written interface decouples controllers from Kiota's
//     generated request-builder hierarchy — handlers depend on
//     domain-shaped methods (`GetForecastAsync`), not on the Kiota
//     surface that may shift across regen runs.

#nullable enable

using ClimaSense.Web.Generated.MLClient.Models;

namespace ClimaSense.Web.ML;

/// <summary>
/// Domain-shaped wrapper around the ml-tier HTTP surface.
///
/// Failure semantics:
///   * <see cref="MLServiceUnavailableException"/> — connection refused / DNS failure.
///   * <see cref="MLServiceBadGatewayException"/>  — ml tier returned 5xx.
///   * <see cref="MLServiceTimeoutException"/>     — request exceeded the bounded timeout.
///
/// All three exceptions are mapped to actionable HTTP responses by
/// <see cref="MLProxyEndpoints"/> (503 / 502 / 504 with a
/// <see cref="ProblemDetails"/> body and no Python traceback).
/// </summary>
public interface IMLServiceClient
{
    /// <summary>GET /api/forecast — bounded 30 s timeout.</summary>
    Task<ForecastEnvelope?> GetForecastAsync(
        int horizonHours,
        CancellationToken cancellationToken = default);

    /// <summary>POST /api/forecast — bounded 30 s timeout.</summary>
    Task<ForecastEnvelope?> PostForecastAsync(
        ForecastRequest body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/anomalies/detect — bounded 60 s timeout for the
    /// changepoint scan-and-replace (longer than the default 30 s).
    /// </summary>
    Task<AnomalyDetectResponse?> PostAnomaliesDetectAsync(
        AnomalyDetectRequest body,
        CancellationToken cancellationToken = default);

    /// <summary>POST /api/profiles/analyze — bounded 30 s timeout.</summary>
    Task<ProfilesAnalyzeResponse?> PostProfilesAnalyzeAsync(
        ProfilesAnalyzeRequest body,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/comfort/score — bounded 30 s timeout.</summary>
    Task<ComfortScoreResponse?> GetComfortScoreAsync(
        int hours,
        CancellationToken cancellationToken = default);
}
