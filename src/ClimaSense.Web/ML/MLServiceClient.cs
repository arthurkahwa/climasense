// SPDX-License-Identifier: MIT
//
// MLServiceClient — hand-written `IMLServiceClient` implementation.
//
// Wraps the Kiota-generated `MLApiClient` request-builder tree
// (`src/ClimaSense.Web/Generated/MLClient/`) under a domain-shaped
// interface. Controllers depend on `IMLServiceClient`, not on Kiota.
//
// Pipeline:
//   HttpClientFactory → HttpClient
//     ├ RequestIdPropagationHandler  (mirrors X-Request-ID to ml tier)
//     ├ MLFailureMappingHandler      (5xx → MLServiceBadGatewayException, etc.)
//     └ HttpClientHandler            (default)
//   ↓
//   HttpClientRequestAdapter (Kiota)
//   ↓
//   MLApiClient (Kiota)
//   ↓
//   Per-call request builders (.Api.Forecast.GetAsync, etc.)
//
// Failure semantics are owned by `MLFailureMappingHandler` — Kiota's
// own ApiException machinery is wrapped by our named exceptions, so
// controllers never need to import any Kiota types.

#nullable enable

using ClimaSense.Web.Generated.MLClient;
using ClimaSense.Web.Generated.MLClient.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace ClimaSense.Web.ML;

public sealed class MLServiceClient : IMLServiceClient
{
    /// <summary>Default per-request bounded timeout (30 s).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Longer timeout for the changepoint scan (60 s).</summary>
    public static readonly TimeSpan ChangepointTimeout = TimeSpan.FromSeconds(60);

    private readonly MLApiClient _client;
    private readonly IRequestAdapter _adapter;

    public MLServiceClient(HttpClient http, IConfiguration config)
    {
        // The HttpClient comes from IHttpClientFactory with the two
        // delegating handlers already attached (see Program.cs).
        var baseUrl = config["CLIMASENSE_ML_BASE_URL"] ?? "http://ml:8000";
        if (http.BaseAddress is null)
        {
            http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        }

        _adapter = new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(),
            httpClient: http);
        _adapter.BaseUrl = http.BaseAddress?.ToString().TrimEnd('/') ?? baseUrl;

        _client = new MLApiClient(_adapter);
    }

    public async Task<ForecastEnvelope?> GetForecastAsync(
        int horizonHours,
        CancellationToken cancellationToken = default)
    {
        using var cts = LinkTimeout(cancellationToken, DefaultTimeout);
        return await _client.Api.Forecast.GetAsync(
            cfg => cfg.QueryParameters.HorizonHours = horizonHours,
            cts.Token).ConfigureAwait(false);
    }

    public async Task<ForecastEnvelope?> PostForecastAsync(
        ForecastRequest body,
        CancellationToken cancellationToken = default)
    {
        using var cts = LinkTimeout(cancellationToken, DefaultTimeout);
        return await _client.Api.Forecast.PostAsync(body, cancellationToken: cts.Token)
            .ConfigureAwait(false);
    }

    public async Task<AnomalyDetectResponse?> PostAnomaliesDetectAsync(
        AnomalyDetectRequest body,
        CancellationToken cancellationToken = default)
    {
        using var cts = LinkTimeout(cancellationToken, ChangepointTimeout);
        return await _client.Api.Anomalies.Detect.PostAsync(body, cancellationToken: cts.Token)
            .ConfigureAwait(false);
    }

    public async Task<ProfilesAnalyzeResponse?> PostProfilesAnalyzeAsync(
        ProfilesAnalyzeRequest body,
        CancellationToken cancellationToken = default)
    {
        using var cts = LinkTimeout(cancellationToken, DefaultTimeout);
        return await _client.Api.Profiles.Analyze.PostAsync(body, cancellationToken: cts.Token)
            .ConfigureAwait(false);
    }

    public async Task<ComfortScoreResponse?> GetComfortScoreAsync(
        int hours,
        CancellationToken cancellationToken = default)
    {
        using var cts = LinkTimeout(cancellationToken, DefaultTimeout);
        return await _client.Api.Comfort.Score.GetAsync(
            cfg => cfg.QueryParameters.Hours = hours,
            cts.Token).ConfigureAwait(false);
    }

    private static CancellationTokenSource LinkTimeout(
        CancellationToken outer, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        return cts;
    }
}
