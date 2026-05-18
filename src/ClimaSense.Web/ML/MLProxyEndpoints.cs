// SPDX-License-Identifier: MIT
//
// MLProxyEndpoints — minimal slice-2 surface exposing the ml-tier
// contract through the web tier, so the failure-mapping handler can be
// exercised end-to-end by a reviewer's curl.
//
// Each endpoint:
//   * Calls IMLServiceClient.
//   * Catches the named MLServiceClient* exceptions and maps them to
//     503 / 502 / 504 with a ProblemDetails body.
//   * Returns 501 with a ProblemDetails body when the ml tier responds
//     501 (stubs).
//
// AC tie-in: AC#6 — "MLServiceClient calls against a stopped ML
// container return HTTP 503 with body `{error: ml_service_unavailable, …}`
// to the browser."

#nullable enable

using System.Collections.Generic;
using ClimaSense.Web.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Kiota.Abstractions;
using AnomalyDetectRequest = ClimaSense.Web.Generated.MLClient.Models.AnomalyDetectRequest;
using AnomalyType = ClimaSense.Web.Generated.MLClient.Models.AnomalyType;
using ForecastRequest = ClimaSense.Web.Generated.MLClient.Models.ForecastRequest;
using KiotaProblemDetails = ClimaSense.Web.Generated.MLClient.Models.ProblemDetails;
using ProfilesAnalyzeRequest = ClimaSense.Web.Generated.MLClient.Models.ProfilesAnalyzeRequest;

namespace ClimaSense.Web.ML;

/// <summary>
/// Wire-shape DTO for proxy-endpoint error responses. Matches the
/// contract's <c>ProblemDetails</c> schema (camelCase via the global
/// <c>JsonNamingPolicy.CamelCase</c>). We deliberately do NOT reuse
/// the Kiota-generated <c>ProblemDetails</c> here because Kiota
/// derives that type from <c>ApiException</c> and renames the
/// <c>message</c> field to <c>MessageEscaped</c> to dodge a base-class
/// collision — neither plays nicely with <c>System.Text.Json</c>'s
/// reflective serialization on a response path.
/// </summary>
public sealed record WireProblemDetails(string Error, string Message, string? RequestId);

public static class MLProxyEndpoints
{
    public static IEndpointRouteBuilder MapMLProxy(this IEndpointRouteBuilder app)
    {
        // GET /api/ml/forecast?horizonHours=72
        app.MapGet("/api/ml/forecast", async (
            HttpContext ctx,
            IMLServiceClient ml,
            CancellationToken ct,
            [FromQuery] int? horizonHours) =>
        {
            try
            {
                var env = await ml.GetForecastAsync(horizonHours ?? 72, ct).ConfigureAwait(false);
                return env is null
                    ? NotImplemented(ctx, "getForecast")
                    : Results.Ok(env);
            }
            catch (KiotaProblemDetails kpd)
            {
                // Kiota raises this on any documented error response code
                // for which we registered an error mapping (501, 502, 503, 504).
                // Slice 2: every contract endpoint returns 501 in practice.
                return MapKiotaProblemDetails(ctx, kpd);
            }
            catch (Exception ex) when (ex is MLServiceUnavailableException
                                          or MLServiceBadGatewayException
                                          or MLServiceTimeoutException)
            {
                return MapFailure(ctx, ex);
            }
        });

        // POST /api/ml/forecast { horizonHours }
        app.MapPost("/api/ml/forecast", async (
            HttpContext ctx,
            IMLServiceClient ml,
            CancellationToken ct,
            ForecastRequest body) =>
        {
            try
            {
                var env = await ml.PostForecastAsync(body, ct).ConfigureAwait(false);
                return env is null
                    ? NotImplemented(ctx, "postForecast")
                    : Results.Ok(env);
            }
            catch (KiotaProblemDetails kpd)
            {
                // Kiota raises this on any documented error response code
                // for which we registered an error mapping (501, 502, 503, 504).
                // Slice 2: every contract endpoint returns 501 in practice.
                return MapKiotaProblemDetails(ctx, kpd);
            }
            catch (Exception ex) when (ex is MLServiceUnavailableException
                                          or MLServiceBadGatewayException
                                          or MLServiceTimeoutException)
            {
                return MapFailure(ctx, ex);
            }
        });

        // POST /api/ml/run/forecast — slice 5's explicit "Emit Forecast" UI
        // path. Same proxy semantics as /api/ml/forecast (POST), kept
        // separate so the button label (`Emit Forecast (72h)`) and the
        // URL match per issue #7 §"What to build". Default body of
        // `{ horizonHours: 72 }` is honoured when the caller omits one.
        app.MapPost("/api/ml/run/forecast", async (
            HttpContext ctx,
            IMLServiceClient ml,
            CancellationToken ct,
            ForecastRequest? body) =>
        {
            try
            {
                var effectiveBody = body ?? new ForecastRequest { HorizonHours = 72 };
                var env = await ml.PostForecastAsync(effectiveBody, ct).ConfigureAwait(false);
                return env is null
                    ? NotImplemented(ctx, "postForecast")
                    : Results.Ok(env);
            }
            catch (KiotaProblemDetails kpd)
            {
                return MapKiotaProblemDetails(ctx, kpd);
            }
            catch (Exception ex) when (ex is MLServiceUnavailableException
                                          or MLServiceBadGatewayException
                                          or MLServiceTimeoutException)
            {
                return MapFailure(ctx, ex);
            }
        });

        // POST /api/ml/anomalies/detect
        app.MapPost("/api/ml/anomalies/detect", async (
            HttpContext ctx,
            IMLServiceClient ml,
            CancellationToken ct,
            AnomalyDetectRequest body) =>
        {
            try
            {
                var resp = await ml.PostAnomaliesDetectAsync(body, ct).ConfigureAwait(false);
                return resp is null
                    ? NotImplemented(ctx, "postAnomaliesDetect")
                    : Results.Ok(resp);
            }
            catch (KiotaProblemDetails kpd)
            {
                // Kiota raises this on any documented error response code
                // for which we registered an error mapping (501, 502, 503, 504).
                // Slice 2: every contract endpoint returns 501 in practice.
                return MapKiotaProblemDetails(ctx, kpd);
            }
            catch (Exception ex) when (ex is MLServiceUnavailableException
                                          or MLServiceBadGatewayException
                                          or MLServiceTimeoutException)
            {
                return MapFailure(ctx, ex);
            }
        });

        // POST /api/ml/profiles/analyze
        app.MapPost("/api/ml/profiles/analyze", async (
            HttpContext ctx,
            IMLServiceClient ml,
            CancellationToken ct,
            ProfilesAnalyzeRequest body) =>
        {
            try
            {
                var resp = await ml.PostProfilesAnalyzeAsync(body, ct).ConfigureAwait(false);
                return resp is null
                    ? NotImplemented(ctx, "postProfilesAnalyze")
                    : Results.Ok(resp);
            }
            catch (KiotaProblemDetails kpd)
            {
                // Kiota raises this on any documented error response code
                // for which we registered an error mapping (501, 502, 503, 504).
                // Slice 2: every contract endpoint returns 501 in practice.
                return MapKiotaProblemDetails(ctx, kpd);
            }
            catch (Exception ex) when (ex is MLServiceUnavailableException
                                          or MLServiceBadGatewayException
                                          or MLServiceTimeoutException)
            {
                return MapFailure(ctx, ex);
            }
        });

        // GET /api/ml/comfort/score?hours=24
        app.MapGet("/api/ml/comfort/score", async (
            HttpContext ctx,
            IMLServiceClient ml,
            CancellationToken ct,
            [FromQuery] int? hours) =>
        {
            try
            {
                var resp = await ml.GetComfortScoreAsync(hours ?? 24, ct).ConfigureAwait(false);
                return resp is null
                    ? NotImplemented(ctx, "getComfortScore")
                    : Results.Ok(resp);
            }
            catch (KiotaProblemDetails kpd)
            {
                // Kiota raises this on any documented error response code
                // for which we registered an error mapping (501, 502, 503, 504).
                // Slice 2: every contract endpoint returns 501 in practice.
                return MapKiotaProblemDetails(ctx, kpd);
            }
            catch (Exception ex) when (ex is MLServiceUnavailableException
                                          or MLServiceBadGatewayException
                                          or MLServiceTimeoutException)
            {
                return MapFailure(ctx, ex);
            }
        });

        // POST /api/ml/run/profiles — slice 9's "Run Profiles" UI path.
        // Per issue #11: "POST /api/ml/run/profiles proxied by .NET to
        // FastAPI's POST /api/profiles/analyze { startDate, endDate }".
        // The ml-tier handler recomputes DayProfiles via the SQL CASE
        // classifier baked into init-db.sql. Idempotent on the range
        // (compute is deterministic + MERGE is keyed on Date). When the
        // caller omits a body the proxy defaults to "the last 7 cursor
        // days at the .NET tier's WallClock" — matches the nightly
        // scheduler's lookback so manual + scheduled runs converge.
        app.MapPost("/api/ml/run/profiles", async (
            HttpContext ctx,
            IMLServiceClient ml,
            ClimaSense.Web.Clock.IClock clock,
            CancellationToken ct,
            ProfilesAnalyzeRequest? body) =>
        {
            try
            {
                var effectiveBody = body;
                if (effectiveBody is null)
                {
                    var endDate = DateOnly.FromDateTime(clock.UtcNow());
                    var startDate = endDate.AddDays(-6);
                    effectiveBody = new ProfilesAnalyzeRequest
                    {
                        StartDate = startDate,
                        EndDate = endDate,
                    };
                }
                var resp = await ml.PostProfilesAnalyzeAsync(effectiveBody, ct).ConfigureAwait(false);
                return resp is null
                    ? NotImplemented(ctx, "postProfilesAnalyze")
                    : Results.Ok(resp);
            }
            catch (KiotaProblemDetails kpd)
            {
                return MapKiotaProblemDetails(ctx, kpd);
            }
            catch (Exception ex) when (ex is MLServiceUnavailableException
                                          or MLServiceBadGatewayException
                                          or MLServiceTimeoutException)
            {
                return MapFailure(ctx, ex);
            }
        });

        // POST /api/ml/run/anomalies — slice 8's "Run Anomalies" UI path.
        // Per issue #10: "POST /api/ml/run/anomalies proxied by .NET to
        // FastAPI's POST /api/anomalies/detect { start_date, end_date }".
        // The ml-tier handler runs the three-detector pipeline at the
        // current cursor and returns an AnomalyDetectResponse envelope
        // verbatim. Idempotent on the cursor (per-type idempotency in
        // the writer). Default body of `{ types: [all-three] }` is
        // honoured when the caller omits one.
        app.MapPost("/api/ml/run/anomalies", async (
            HttpContext ctx,
            IMLServiceClient ml,
            CancellationToken ct,
            AnomalyDetectRequest? body) =>
        {
            try
            {
                var effectiveBody = body ?? new AnomalyDetectRequest
                {
                    Types = new List<AnomalyType?>
                    {
                        AnomalyType.Sensor_failure,
                        AnomalyType.Residual_outlier,
                        AnomalyType.Regime_shift,
                    },
                };
                var resp = await ml.PostAnomaliesDetectAsync(effectiveBody, ct).ConfigureAwait(false);
                return resp is null
                    ? NotImplemented(ctx, "postAnomaliesDetect")
                    : Results.Ok(resp);
            }
            catch (KiotaProblemDetails kpd)
            {
                return MapKiotaProblemDetails(ctx, kpd);
            }
            catch (Exception ex) when (ex is MLServiceUnavailableException
                                          or MLServiceBadGatewayException
                                          or MLServiceTimeoutException)
            {
                return MapFailure(ctx, ex);
            }
        });

        // POST /api/ml/run/comfort — slice 7's "Run Comfort" UI path.
        // Per issue #9: "POST /api/ml/run/comfort proxied by .NET to
        // FastAPI's GET /api/comfort/score?hours=24". The ml-tier
        // handler is real (slice 7) and MERGEs into ComfortScores; the
        // proxy returns the resulting envelope verbatim. Idempotent on
        // the cursor's bucket (calculator is pure).
        app.MapPost("/api/ml/run/comfort", async (
            HttpContext ctx,
            IMLServiceClient ml,
            CancellationToken ct,
            [FromQuery] int? hours) =>
        {
            try
            {
                var resp = await ml.GetComfortScoreAsync(hours ?? 24, ct).ConfigureAwait(false);
                return resp is null
                    ? NotImplemented(ctx, "getComfortScore")
                    : Results.Ok(resp);
            }
            catch (KiotaProblemDetails kpd)
            {
                return MapKiotaProblemDetails(ctx, kpd);
            }
            catch (Exception ex) when (ex is MLServiceUnavailableException
                                          or MLServiceBadGatewayException
                                          or MLServiceTimeoutException)
            {
                return MapFailure(ctx, ex);
            }
        });

        return app;
    }

    private static IResult NotImplemented(HttpContext ctx, string operationId) =>
        Results.Json(
            new WireProblemDetails(
                Error: "not_implemented",
                Message: $"ml-tier returned 501 for {operationId} (stub).",
                RequestId: RequestIdMiddleware.Get(ctx)),
            statusCode: StatusCodes.Status501NotImplemented);

    /// <summary>
    /// Translate a Kiota-thrown `ProblemDetails` (an `ApiException` subclass)
    /// into a typed HTTP response. The wire `error` slug is preserved verbatim
    /// from the ml-tier emission so `not_implemented` round-trips intact.
    /// </summary>
    public static IResult MapKiotaProblemDetails(HttpContext ctx, KiotaProblemDetails kpd)
    {
        // Kiota populates `ResponseStatusCode` on the ApiException base.
        var statusCode = kpd.ResponseStatusCode > 0
            ? kpd.ResponseStatusCode
            : StatusCodes.Status501NotImplemented;

        return Results.Json(
            new WireProblemDetails(
                Error: kpd.Error ?? "ml_service_error",
                Message: kpd.MessageEscaped ?? kpd.Message,
                RequestId: RequestIdMiddleware.Get(ctx)),
            statusCode: statusCode);
    }

    /// <summary>
    /// Convert an MLServiceClient* exception into a canonical ProblemDetails response.
    /// Public so unit tests can exercise the mapping in isolation.
    /// </summary>
    public static IResult MapFailure(HttpContext ctx, Exception ex)
    {
        var requestId = RequestIdMiddleware.Get(ctx);

        return ex switch
        {
            MLServiceUnavailableException =>
                Results.Json(
                    new WireProblemDetails(
                        Error: "ml_service_unavailable",
                        Message: "ml tier unreachable (connection refused / dns failure).",
                        RequestId: requestId),
                    statusCode: StatusCodes.Status503ServiceUnavailable),

            MLServiceBadGatewayException badGw =>
                Results.Json(
                    new WireProblemDetails(
                        Error: "ml_service_error",
                        Message: $"ml tier returned {(int)badGw.UpstreamStatus} {badGw.UpstreamStatus}.",
                        RequestId: requestId),
                    statusCode: StatusCodes.Status502BadGateway),

            MLServiceTimeoutException timeout =>
                Results.Json(
                    new WireProblemDetails(
                        Error: "ml_service_timeout",
                        Message: $"ml tier exceeded the bounded timeout of {timeout.Timeout.TotalSeconds:F0}s.",
                        RequestId: requestId),
                    statusCode: StatusCodes.Status504GatewayTimeout),

            _ => Results.Json(
                new WireProblemDetails(
                    Error: "ml_service_error",
                    Message: "Unexpected ml-tier failure.",
                    RequestId: requestId),
                statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
