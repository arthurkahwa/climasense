using System.Diagnostics.CodeAnalysis;

namespace ClimaSense.Web.Logging;

/// <summary>
/// Mints or accepts the <c>X-Request-ID</c> header on every inbound HTTP
/// request and pushes it into the active log scope so every line emitted
/// during the request carries the correlation key.
/// </summary>
/// <remarks>
/// Emission policy:
/// <list type="bullet">
///   <item>If the inbound request carries <c>X-Request-ID</c>, the value
///         is honoured verbatim (after a defensive 1-128 char ASCII guard).</item>
///   <item>Otherwise a fresh GUID-N (32 chars, no hyphens) is minted.</item>
///   <item>The chosen value is mirrored back on the response so curl /
///         the browser can correlate.</item>
///   <item>The value is exposed as <see cref="HttpContext.Items"/>[<see cref="RequestIdKey"/>]
///         and as a logger scope key <c>request_id</c>.</item>
/// </list>
/// </remarks>
public sealed class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-ID";
    public const string RequestIdKey = "ClimaSense.RequestId";
    public const string LogScopeKey = "request_id";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = ExtractInbound(context) ?? Mint();

        context.Items[RequestIdKey] = requestId;
        context.Response.Headers[HeaderName] = requestId;

        var scopeState = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [LogScopeKey] = requestId,
        };

        using (_logger.BeginScope(scopeState))
        {
            await _next(context);
        }
    }

    private static string? ExtractInbound(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return null;
        }

        var raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Defensive guard: only allow 1-128 visible ASCII characters. Reject
        // CR/LF (header injection) and anything longer than 128 chars.
        if (raw.Length > 128)
        {
            return null;
        }

        foreach (var c in raw)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return null;
            }
        }

        return raw;
    }

    private static string Mint() => Guid.NewGuid().ToString("N");

    /// <summary>Read the current request's correlation ID from <see cref="HttpContext"/>.</summary>
    [SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = "Helper method.")]
    public static string? Get(HttpContext? context) =>
        context?.Items.TryGetValue(RequestIdKey, out var value) == true ? value as string : null;
}
