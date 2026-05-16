// SPDX-License-Identifier: MIT
//
// MLFailureMappingHandler — `DelegatingHandler` that converts the
// underlying socket / timeout / upstream-5xx outcomes into the named
// MLServiceClient exceptions documented in `IMLServiceClient`.
//
// The mapping is intentionally narrow: anything we don't recognise
// is bubbled up unchanged so callers see the real failure rather
// than a misleading wrapper.

#nullable enable

using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ClimaSense.Web.ML;

public sealed class MLFailureMappingHandler : DelegatingHandler
{
    private readonly ILogger<MLFailureMappingHandler> _logger;

    public MLFailureMappingHandler(ILogger<MLFailureMappingHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient throws TaskCanceledException on timeout (with
            // its own CancellationToken). When OUR token wasn't cancelled,
            // this is a timeout — map it.
            var timeout = request.Options.TryGetValue(
                MLClientHttpOptions.TimeoutKey, out var t)
                ? t : TimeSpan.FromSeconds(30);

            _logger.LogWarning(
                "ml-tier timeout after {Timeout} on {Uri}",
                timeout,
                request.RequestUri);

            throw new MLServiceTimeoutException(
                timeout,
                $"ml-tier request to {request.RequestUri} exceeded {timeout.TotalSeconds:F0}s",
                ex);
        }
        catch (HttpRequestException ex) when (
            IsConnectionFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "ml-tier unreachable on {Uri}: {Message}",
                request.RequestUri,
                ex.Message);

            throw new MLServiceUnavailableException(
                $"ml-tier unreachable at {request.RequestUri}",
                ex);
        }

        // Upstream 5xx is mapped to 502 by the caller layer — EXCEPT 501,
        // which is "not implemented yet" and is a legitimate stub response
        // in slice 2. 501 passes through unchanged so the Kiota client's
        // error mapping turns it into a typed `ProblemDetails` exception
        // the proxy endpoint surfaces as 501 to the browser.
        var statusCode = (int)response.StatusCode;
        if (statusCode >= 500 && statusCode <= 599 && statusCode != 501)
        {
            _logger.LogWarning(
                "ml-tier returned {Status} on {Uri}",
                statusCode,
                request.RequestUri);

            // We surface a typed exception (not the response) so the
            // .NET caller never accidentally tries to deserialize the
            // Python traceback into a strongly-typed DTO.
            var status = response.StatusCode;
            response.Dispose();
            throw new MLServiceBadGatewayException(
                status,
                $"ml-tier responded {statusCode} on {request.RequestUri}");
        }

        return response;
    }

    private static bool IsConnectionFailure(HttpRequestException ex)
    {
        // HttpRequestException's StatusCode is null only for transport-level
        // failures (DNS, refused, reset). Anything with a status code
        // is by definition a parsed response — those don't belong here.
        if (ex.StatusCode != null)
        {
            return false;
        }

        // Walk the inner-exception chain looking for a SocketException
        // (connection refused, host unreachable, etc.).
        for (var e = ex.InnerException; e is not null; e = e.InnerException)
        {
            if (e is SocketException)
            {
                return true;
            }
        }

        // No inner SocketException — assume it's a connection failure
        // anyway (e.g. SslException, IOException with no socket inner).
        // This is conservative: better to map it as 503 than to leak
        // the raw HttpRequestException to the browser.
        return true;
    }
}

/// <summary>
/// Keys for ambient state carried on <see cref="HttpRequestMessage.Options"/>
/// across the HttpClient pipeline.
/// </summary>
public static class MLClientHttpOptions
{
    /// <summary>
    /// Per-request bounded timeout. Set by <see cref="MLServiceClient"/>
    /// before dispatch; read by <see cref="MLFailureMappingHandler"/> when
    /// composing the <see cref="MLServiceTimeoutException"/> message.
    /// </summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> TimeoutKey = new("ClimaSense.ML.Timeout");
}
