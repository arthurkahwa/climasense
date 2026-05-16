using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.ML;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClimaSense.Web.Tests;

/// <summary>
/// Unit tests for <see cref="MLFailureMappingHandler"/>.
///
/// Covers AC #6: "MLServiceClient calls against a stopped ML container
/// return HTTP 503 with body `{error: ml_service_unavailable, ...}`"
/// at the handler layer — the proxy endpoint then converts the
/// thrown exception into the documented 503 / 502 / 504 response.
/// </summary>
public sealed class MLFailureMappingTests
{
    /// <summary>Stub inner handler that always throws the supplied exception.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Func<Exception> _exFactory;
        public ThrowingHandler(Func<Exception> exFactory) => _exFactory = exFactory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _exFactory();
    }

    /// <summary>Stub inner handler that returns a synthetic response.</summary>
    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public StaticResponseHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status));
    }

    private static HttpClient Wrap(HttpMessageHandler inner)
    {
        var mapping = new MLFailureMappingHandler(NullLogger<MLFailureMappingHandler>.Instance)
        {
            InnerHandler = inner,
        };
        return new HttpClient(mapping)
        {
            BaseAddress = new Uri("http://ml-test:8000"),
        };
    }

    [Fact]
    public async Task Socket_connection_refused_maps_to_MLServiceUnavailableException()
    {
        var inner = new ThrowingHandler(() =>
            new HttpRequestException(
                "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused)));

        using var client = Wrap(inner);

        await Assert.ThrowsAsync<MLServiceUnavailableException>(() =>
            client.GetAsync("/api/forecast"));
    }

    [Fact]
    public async Task Dns_failure_maps_to_MLServiceUnavailableException()
    {
        var inner = new ThrowingHandler(() =>
            new HttpRequestException(
                "DNS failure",
                new SocketException((int)SocketError.HostNotFound)));

        using var client = Wrap(inner);

        await Assert.ThrowsAsync<MLServiceUnavailableException>(() =>
            client.GetAsync("/api/forecast"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Upstream_5xx_maps_to_MLServiceBadGatewayException(
        HttpStatusCode upstream)
    {
        var inner = new StaticResponseHandler(upstream);
        using var client = Wrap(inner);

        var ex = await Assert.ThrowsAsync<MLServiceBadGatewayException>(() =>
            client.GetAsync("/api/forecast"));

        Assert.Equal(upstream, ex.UpstreamStatus);
    }

    [Fact]
    public async Task Inner_TaskCanceledException_with_uncancelled_outer_token_maps_to_timeout()
    {
        // HttpClient throws TaskCanceledException on its internal timeout.
        var inner = new ThrowingHandler(() =>
            new TaskCanceledException("simulated httpclient timeout"));

        using var client = Wrap(inner);

        // We do NOT cancel the outer CT — that's the discriminator.
        await Assert.ThrowsAsync<MLServiceTimeoutException>(() =>
            client.GetAsync("/api/forecast", CancellationToken.None));
    }

    [Fact]
    public async Task Outer_token_cancellation_does_NOT_map_to_timeout()
    {
        // If our token *is* cancelled, the TaskCanceledException is genuine
        // caller-side cancellation and must propagate unchanged.
        var inner = new ThrowingHandler(() =>
            new TaskCanceledException("caller cancelled"));

        using var client = Wrap(inner);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetAsync("/api/forecast", cts.Token));
    }

    [Fact]
    public async Task Successful_2xx_response_passes_through_unchanged()
    {
        var inner = new StaticResponseHandler(HttpStatusCode.OK);
        using var client = Wrap(inner);

        using var response = await client.GetAsync("/api/forecast");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
