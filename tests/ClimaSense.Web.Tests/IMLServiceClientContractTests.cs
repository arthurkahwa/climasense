using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClimaSense.Web.Generated.MLClient.Models;
using ClimaSense.Web.ML;
using Xunit;

namespace ClimaSense.Web.Tests;

/// <summary>
/// Locks the <see cref="IMLServiceClient"/> surface shape so a slice-7
/// implementer can't silently rename methods, drop a CancellationToken
/// parameter, or change return types without a deliberate update here.
///
/// Five methods, one per contract endpoint declared in
/// <c>contracts/openapi.yaml</c>. Each method takes a typed body or
/// query param plus an optional <see cref="CancellationToken"/>.
/// </summary>
public sealed class IMLServiceClientContractTests
{
    private static MethodInfo Method(string name) =>
        typeof(IMLServiceClient).GetMethod(name)
        ?? throw new Xunit.Sdk.XunitException(
            $"IMLServiceClient.{name} not found — surface drift.");

    [Fact]
    public void Five_methods_exist()
    {
        var methods = typeof(IMLServiceClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "GetComfortScoreAsync",
                "GetForecastAsync",
                "PostAnomaliesDetectAsync",
                "PostForecastAsync",
                "PostProfilesAnalyzeAsync",
            },
            methods);
    }

    [Fact]
    public void Every_method_takes_a_CancellationToken()
    {
        foreach (var m in typeof(IMLServiceClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.Contains(
                m.GetParameters(),
                p => p.ParameterType == typeof(CancellationToken));
        }
    }

    [Fact]
    public void Return_types_are_Task_of_typed_envelope()
    {
        Assert.Equal(typeof(Task<ForecastEnvelope?>),
            Method(nameof(IMLServiceClient.GetForecastAsync)).ReturnType);
        Assert.Equal(typeof(Task<ForecastEnvelope?>),
            Method(nameof(IMLServiceClient.PostForecastAsync)).ReturnType);
        Assert.Equal(typeof(Task<AnomalyDetectResponse?>),
            Method(nameof(IMLServiceClient.PostAnomaliesDetectAsync)).ReturnType);
        Assert.Equal(typeof(Task<ProfilesAnalyzeResponse?>),
            Method(nameof(IMLServiceClient.PostProfilesAnalyzeAsync)).ReturnType);
        Assert.Equal(typeof(Task<ComfortScoreResponse?>),
            Method(nameof(IMLServiceClient.GetComfortScoreAsync)).ReturnType);
    }
}
