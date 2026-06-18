using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClimaSense.Monitor.Services;

public sealed class DbFeedHealthCheck(ReadingsService svc) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var s = await svc.GetLatestStatusAsync(ct);
            if (s is null) return HealthCheckResult.Degraded("no readings");
            return s.Value.IsStale
                ? HealthCheckResult.Degraded($"feed stale ({s.Value.MinutesOld} min)")
                : HealthCheckResult.Healthy($"fresh ({s.Value.MinutesOld} min)");
        }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("db error", ex); }
    }
}
