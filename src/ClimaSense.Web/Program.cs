using System.Text;
using System.Text.Json;
using ClimaSense.Web.Clock;
using ClimaSense.Web.Cursor;
using ClimaSense.Web.Logging;
using ClimaSense.Web.ML;
using ClimaSense.Web.Sse;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------
// Logging: structured JSON to stdout. One line per event.
// ---------------------------------------------------------------------
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.FormatterName = JsonStdoutFormatter.FormatterName;
});
builder.Logging.AddConsoleFormatter<JsonStdoutFormatter, ConsoleFormatterOptions>();
builder.Logging.Configure(options =>
{
    options.ActivityTrackingOptions = ActivityTrackingOptions.None;
});

// ---------------------------------------------------------------------
// JSON: camelCase on the wire (per PRD; .NET ↔ Python ↔ browser).
// ---------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
});

// ---------------------------------------------------------------------
// Clock + cursor wiring.
//
// Slice 1 only ships WallClock. The ReplayClock implementation arrives
// in slice 12 (#14); this is the registration site that will swap the
// concrete IClock based on CLIMASENSE_CLOCK_MODE at that point.
// ---------------------------------------------------------------------
// TODO(slice-12): switch IClock between WallClock and ReplayClock
// based on Environment.GetEnvironmentVariable("CLIMASENSE_CLOCK_MODE").
builder.Services.AddSingleton<IClock, WallClock>();
builder.Services.AddScoped<CursorSnapshot>(sp =>
    CursorSnapshot.FromClock(sp.GetRequiredService<IClock>()));

// ---------------------------------------------------------------------
// SSE: AlertStream singleton + heartbeat hosted service.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<AlertStream>();
builder.Services.AddHostedService<HeartbeatService>();

// ---------------------------------------------------------------------
// ML tier client: HttpClientFactory pipeline with the two delegating
// handlers (X-Request-ID propagation + failure mapping) on the
// transport. Per the slice-2 contract:
//   * Default per-request timeout: 30 s.
//   * Anomalies/detect overrides to 60 s at the call site.
//   * No automatic retries.
//
// `HttpClient.Timeout` is set conservatively at 90 s (the upper bound
// across both call sites + DelegatingHandler overhead) — the actual
// bounded timeout is enforced per-call via a linked CancellationToken
// inside `MLServiceClient`. This is the canonical pattern in .NET 10
// because HttpClient.Timeout cannot be varied per request.
// ---------------------------------------------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<RequestIdPropagationHandler>();
builder.Services.AddTransient<MLFailureMappingHandler>();

builder.Services
    .AddHttpClient<IMLServiceClient, MLServiceClient>(http =>
    {
        var baseUrl = builder.Configuration["CLIMASENSE_ML_BASE_URL"] ?? "http://ml:8000";
        http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        http.Timeout = TimeSpan.FromSeconds(90);  // outer envelope; per-call timeout is tighter.
    })
    .AddHttpMessageHandler<RequestIdPropagationHandler>()
    .AddHttpMessageHandler<MLFailureMappingHandler>();

// ---------------------------------------------------------------------
// Razor Pages — single placeholder Index page.
// ---------------------------------------------------------------------
builder.Services.AddRazorPages();

var app = builder.Build();

// ---------------------------------------------------------------------
// Pipeline.
// ---------------------------------------------------------------------
app.UseMiddleware<RequestIdMiddleware>();
app.UseStaticFiles();
app.MapRazorPages();

// ml-tier proxy endpoints (slice 2). Demonstrate the failure-mapping
// pipeline — 503 / 502 / 504 with a ProblemDetails body and no Python
// traceback. The slice-2 ml tier returns 501 for every contract
// endpoint, so a successful call here surfaces as 501 to the browser.
app.MapMLProxy();

// Liveness — process up.
app.MapGet("/api/health/live", (HttpContext ctx, IClock clock) =>
{
    return Results.Json(new
    {
        status = "ok",
        service = "web",
        ts = clock.UtcNow().ToString("O"),
    });
});

// Readiness — DB connectivity probe.
app.MapGet("/api/health/ready", async (
    HttpContext ctx,
    IClock clock,
    IConfiguration config,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("HealthReady");
    var connStr = BuildConnectionString(config);

    var checks = new Dictionary<string, string>(StringComparer.Ordinal);
    var dbOk = false;

    try
    {
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        dbOk = result is not null;
        checks["db"] = dbOk ? "ok" : "fail";
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Readiness DB probe failed");
        checks["db"] = "fail";
    }

    var status = dbOk ? "ok" : "unavailable";
    var statusCode = dbOk ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;

    return Results.Json(
        new
        {
            status,
            service = "web",
            ts = clock.UtcNow().ToString("O"),
            checks,
        },
        statusCode: statusCode);
});

// SSE endpoint. Each connection is one Subscriber; closure unsubscribes.
app.MapGet("/api/alerts/stream", async (
    HttpContext context,
    AlertStream stream,
    ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("AlertStream");

    context.Response.Headers["Content-Type"] = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache, no-transform";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    context.Response.Headers["Connection"] = "keep-alive";

    // Disable any response buffering — SSE needs to flush per frame.
    var bufferingFeature = context.Features.Get<IHttpResponseBodyFeature>();
    bufferingFeature?.DisableBuffering();

    long? lastEventId = null;
    if (context.Request.Headers.TryGetValue("Last-Event-ID", out var laid)
        && long.TryParse(laid.ToString(), out var parsed))
    {
        lastEventId = parsed;
    }

    using var subscription = stream.Subscribe(lastEventId);
    logger.LogInformation(
        "SSE subscribed: id={SubscriberId} lastEventId={LastEventId} subscribers={Subscribers}",
        subscription.Id,
        lastEventId,
        stream.SubscriberCount);

    var aborted = context.RequestAborted;

    try
    {
        // Initial comment frame so the EventSource fires `onopen` immediately.
        await context.Response.WriteAsync(": connected\n\n", aborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(aborted).ConfigureAwait(false);

        await foreach (var frame in subscription.Reader.ReadAllAsync(aborted).ConfigureAwait(false))
        {
            var sb = new StringBuilder(64 + frame.DataJson.Length);
            sb.Append("event: ").Append(frame.EventType).Append('\n');
            sb.Append("id: ").Append(frame.EventId).Append('\n');
            sb.Append("data: ").Append(frame.DataJson).Append("\n\n");

            await context.Response.WriteAsync(sb.ToString(), aborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(aborted).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException) when (aborted.IsCancellationRequested)
    {
        // Browser closed the tab.
    }
    finally
    {
        logger.LogInformation(
            "SSE unsubscribed: id={SubscriberId} subscribersAfter={Subscribers}",
            subscription.Id,
            stream.SubscriberCount - 1);
    }
});

app.Logger.LogInformation("ClimaSense.Web starting on {Url}", builder.Configuration["ASPNETCORE_URLS"] ?? "http://+:8080");

app.Run();

static string BuildConnectionString(IConfiguration config)
{
    var host = config["CLIMASENSE_DB_HOST"] ?? "db";
    var port = config["CLIMASENSE_DB_PORT"] ?? "1433";
    var name = config["CLIMASENSE_DB_NAME"] ?? "ClimaSense";
    var user = config["CLIMASENSE_DB_USER"] ?? "sa";
    var pwd = config["CLIMASENSE_DB_PASSWORD"] ?? string.Empty;

    var b = new SqlConnectionStringBuilder
    {
        DataSource = $"{host},{port}",
        InitialCatalog = name,
        UserID = user,
        Password = pwd,
        Encrypt = true,
        TrustServerCertificate = true,
        ConnectTimeout = 5,
    };
    return b.ConnectionString;
}

/// <summary>
/// Public marker class so xUnit's <c>WebApplicationFactory&lt;TEntryPoint&gt;</c>
/// can locate the test host. Sits at the root namespace deliberately —
/// the integration tests construct it as <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program { }
