using ClimaSense.Monitor.Domain;
using ClimaSense.Monitor.Services;

namespace ClimaSense.Monitor.Endpoints;

public static class ReadingsApi
{
    public static IEndpointRouteBuilder MapReadingsApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/readings");

        g.MapGet("/latest", async (ReadingsService svc, CancellationToken ct) =>
        {
            var s = await svc.GetLatestStatusAsync(ct);
            return Results.Ok(s);
        });

        g.MapGet("/series", (string? range, DateTime? from, DateTime? to, ReadingsService svc, IClock clock, CancellationToken ct)
            => Resolve(range, from, to, clock, (f, t) => svc.GetSeriesAsync(f, t, ct)));

        g.MapGet("/daily", (string? range, DateTime? from, DateTime? to, ReadingsService svc, IClock clock, CancellationToken ct)
            => Resolve(range, from, to, clock, (f, t) => svc.GetDailyAsync(f, t, ct)));

        g.MapGet("/excursions", (string? range, DateTime? from, DateTime? to, ReadingsService svc, IClock clock, CancellationToken ct)
            => Resolve(range, from, to, clock, (f, t) => svc.GetExcursionsAsync(f, t, ct)));

        g.MapGet("/raw", (string? range, DateTime? from, DateTime? to, ReadingsService svc, IClock clock, CancellationToken ct)
            => Resolve(range, from, to, clock, (f, t) => svc.GetRawAsync(f, t, ct)));

        app.MapGet("/api/alerts", (string? range, DateTime? from, DateTime? to, ReadingsService svc, IClock clock, CancellationToken ct)
            => Resolve(range, from, to, clock, (f, t) => svc.GetAlertsAsync(f, t, ct)));

        app.MapGet("/api/insights", (string? range, DateTime? from, DateTime? to, InsightsService svc, IClock clock, CancellationToken ct)
            => Resolve(range, from, to, clock, (f, t) => svc.GetInsightsAsync(f, t, ct)));

        return app;
    }

    static async Task<IResult> Resolve<T>(string? range, DateTime? from, DateTime? to, IClock clock,
        Func<DateTime, DateTime, Task<T>> query)
    {
        if (!RangeResolver.TryResolve(range, from, to, CetZone.FromUtc(clock.UtcNow), out var f, out var t, out var err))
            return Results.BadRequest(new { error = err });
        return Results.Ok(await query(f, t));
    }
}
