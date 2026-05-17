// SPDX-License-Identifier: MIT
//
// RequestIdPropagationHandler — copies the inbound X-Request-ID from the
// current HttpContext onto every outbound ml-tier request so the ml
// tier's log lines correlate with the web tier's.

#nullable enable

using ClimaSense.Web.Logging;
using Microsoft.AspNetCore.Http;

namespace ClimaSense.Web.ML;

public sealed class RequestIdPropagationHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public RequestIdPropagationHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var ctx = _httpContextAccessor.HttpContext;
        var requestId = RequestIdMiddleware.Get(ctx);
        if (!string.IsNullOrEmpty(requestId)
            && !request.Headers.Contains(RequestIdMiddleware.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(
                RequestIdMiddleware.HeaderName,
                requestId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
