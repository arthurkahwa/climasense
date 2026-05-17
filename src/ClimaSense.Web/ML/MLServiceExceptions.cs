// SPDX-License-Identifier: MIT
//
// Failure-mode exceptions for IMLServiceClient.
//
// These exist as named types (not bare HttpRequestException) so the
// proxy endpoint layer can map them to canonical HTTP status codes
// without inspecting message strings or status-code numerics. They
// are the single source of truth for the failure mapping documented
// in `contracts/openapi.yaml` (description block under `info`).

#nullable enable

using System.Net;

namespace ClimaSense.Web.ML;

/// <summary>
/// Connection-refused or DNS failure reaching the ml tier.
/// Mapped to HTTP 503 with <c>error: "ml_service_unavailable"</c>.
/// </summary>
public sealed class MLServiceUnavailableException : Exception
{
    public MLServiceUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// The ml tier returned a 5xx response. Mapped to HTTP 502 with
/// <c>error: "ml_service_error"</c>.
/// </summary>
public sealed class MLServiceBadGatewayException : Exception
{
    public HttpStatusCode UpstreamStatus { get; }

    public MLServiceBadGatewayException(HttpStatusCode upstreamStatus, string message)
        : base(message)
    {
        UpstreamStatus = upstreamStatus;
    }
}

/// <summary>
/// The request exceeded the bounded timeout. Mapped to HTTP 504 with
/// <c>error: "ml_service_timeout"</c>.
/// </summary>
public sealed class MLServiceTimeoutException : Exception
{
    public TimeSpan Timeout { get; }

    public MLServiceTimeoutException(TimeSpan timeout, string message, Exception? inner = null)
        : base(message, inner)
    {
        Timeout = timeout;
    }
}
