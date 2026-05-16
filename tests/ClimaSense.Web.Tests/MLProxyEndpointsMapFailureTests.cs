using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using ClimaSense.Web.ML;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClimaSense.Web.Tests;

/// <summary>
/// Unit tests for <see cref="MLProxyEndpoints.MapFailure"/> — the
/// pure exception -> ProblemDetails translation. Locks AC #6:
/// connection-refused -> 503 with `error: ml_service_unavailable`.
///
/// We deserialize into a lightweight local DTO rather than the
/// Kiota-generated <c>ProblemDetails</c> (which is an
/// <see cref="Microsoft.Kiota.Abstractions.ApiException"/> subclass
/// and not a System.Text.Json-friendly POCO).
/// </summary>
public sealed class MLProxyEndpointsMapFailureTests
{
    private sealed record WireProblemDetails(string? Error, string? Message, string? RequestId);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task<(int status, WireProblemDetails body)> Execute(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            })
            .BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Response.Body = new MemoryStream();

        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var raw = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var body = JsonSerializer.Deserialize<WireProblemDetails>(raw, JsonOpts)
            ?? throw new Xunit.Sdk.XunitException(
                $"Failed to deserialize ProblemDetails from '{raw}'");
        return (ctx.Response.StatusCode, body);
    }

    [Fact]
    public async Task Unavailable_exception_yields_503_with_ml_service_unavailable()
    {
        var ctx = new DefaultHttpContext();
        var ex = new MLServiceUnavailableException("ml down");

        var result = MLProxyEndpoints.MapFailure(ctx, ex);
        var (status, body) = await Execute(result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("ml_service_unavailable", body.Error);
    }

    [Fact]
    public async Task BadGateway_exception_yields_502_with_ml_service_error()
    {
        var ctx = new DefaultHttpContext();
        var ex = new MLServiceBadGatewayException(
            HttpStatusCode.InternalServerError, "ml errored");

        var result = MLProxyEndpoints.MapFailure(ctx, ex);
        var (status, body) = await Execute(result);

        Assert.Equal(StatusCodes.Status502BadGateway, status);
        Assert.Equal("ml_service_error", body.Error);
    }

    [Fact]
    public async Task Timeout_exception_yields_504_with_ml_service_timeout()
    {
        var ctx = new DefaultHttpContext();
        var ex = new MLServiceTimeoutException(
            TimeSpan.FromSeconds(30), "ml slow");

        var result = MLProxyEndpoints.MapFailure(ctx, ex);
        var (status, body) = await Execute(result);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, status);
        Assert.Equal("ml_service_timeout", body.Error);
    }
}
