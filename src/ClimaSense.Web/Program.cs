using System.Text;
using System.Text.Json;
using ClimaSense.Web.Alerts;
using ClimaSense.Web.Anomalies;
using ClimaSense.Web.Clock;
using ClimaSense.Web.Comfort;
using ClimaSense.Web.Cursor;
using ClimaSense.Web.Forecasts;
using ClimaSense.Web.Leaderboard;
using ClimaSense.Web.Logging;
using ClimaSense.Web.ML;
using ClimaSense.Web.Profiles;
using ClimaSense.Web.Readings;
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
// Readings — slice 3. `SensorDataService` is scoped (matches the
// CursorSnapshot lifetime); its single seam (`LatestReadingFetcher`)
// is wired to the production SQL adapter via a delegate cast so tests
// can inject a lambda without touching DI.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<SqlLatestReadingFetcher>();
builder.Services.AddScoped<SensorDataService>(sp =>
{
    var sqlFetcher = sp.GetRequiredService<SqlLatestReadingFetcher>();
    return new SensorDataService(sqlFetcher.FetchAsync);
});

// ---------------------------------------------------------------------
// Readings — slice 4. RangeQueryService follows the slice-3 delegate
// pattern with two seams (RangeFetcher + HeatmapFetcher). The raw-window
// cap is read from CLIMASENSE_RAW_MAX_DAYS, defaulting to 7.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<SqlRangeFetcher>();
builder.Services.AddScoped<RangeQueryService>(sp =>
{
    var sqlFetcher = sp.GetRequiredService<SqlRangeFetcher>();
    var cfg = sp.GetRequiredService<IConfiguration>();
    var rawMaxDays = int.TryParse(
        cfg["CLIMASENSE_RAW_MAX_DAYS"],
        out var parsed) && parsed > 0
        ? parsed
        : RangeQueryService.DefaultRawMaxDays;
    return new RangeQueryService(
        sqlFetcher.FetchRangeAsync,
        sqlFetcher.FetchHeatmapAsync,
        rawMaxDays);
});

// ---------------------------------------------------------------------
// Forecasts — slice 5. `ForecastReadService` follows the slice-3/4
// delegate-seam pattern. The read goes through the
// `dbo.fv_forecasts_at_cursor` TVF so cursor-clipping is enforced by
// the schema, not by caller discipline.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<SqlForecastFetcher>();
builder.Services.AddScoped<ForecastReadService>(sp =>
{
    var fetcher = sp.GetRequiredService<SqlForecastFetcher>();
    return new ForecastReadService(fetcher.FetchAsync);
});

// ---------------------------------------------------------------------
// Leaderboard — slice 6. `LeaderboardReadService` follows the same
// delegate-seam pattern. The table is global (no cursor clip);
// `dbo.Leaderboard` is populated at FastAPI startup by the ml-tier's
// `LeaderboardSeeder` and read by this service for the Razor
// `Analysis` page + `GET /api/leaderboard`.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<SqlLeaderboardFetcher>();
builder.Services.AddScoped<LeaderboardReadService>(sp =>
{
    var fetcher = sp.GetRequiredService<SqlLeaderboardFetcher>();
    return new LeaderboardReadService(fetcher.FetchAsync);
});

// ---------------------------------------------------------------------
// Comfort — slice 7. `ComfortReadService` follows the slice-5/6 delegate
// pattern. The read goes through `dbo.fv_comfortscores_at_cursor(@asOf)`
// so cursor-clipping is enforced by the schema. `dbo.ComfortScores` is
// populated by the ml-tier's APScheduler-driven `ComfortEmitter`
// (β-prime gate, one row per replay-hour) plus the on-demand
// `GET /api/comfort/score` endpoint.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<SqlComfortFetcher>();
builder.Services.AddScoped<ComfortReadService>(sp =>
{
    var fetcher = sp.GetRequiredService<SqlComfortFetcher>();
    return new ComfortReadService(fetcher.FetchAsync);
});

// ---------------------------------------------------------------------
// Comfort Budget — slice 10. `ComfortBudgetReadService` follows the
// slice-5/6/7/8/9 delegate-seam pattern. Three pure SQL aggregations
// over `dbo.ComfortScores` + `dbo.DayProfiles` (both cursor-clipped
// via TVFs at the schema level). The discomfort threshold comes from
// `COMFORT_DISCOMFORT_THRESHOLD` (default 70.0) and the window from
// `COMFORT_BUDGET_WINDOW_DAYS` (default 7) per the epic. Both are read
// once at DI construction so a config change requires a process
// restart (acceptable — they're slow-moving knobs, not per-request).
// ---------------------------------------------------------------------
builder.Services.AddSingleton<SqlComfortBudgetFetcher>();
builder.Services.AddScoped<ComfortBudgetReadService>(sp =>
{
    var fetcher = sp.GetRequiredService<SqlComfortBudgetFetcher>();
    var cfg = sp.GetRequiredService<IConfiguration>();
    var threshold = double.TryParse(
        cfg["COMFORT_DISCOMFORT_THRESHOLD"],
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out var parsedThreshold)
        && parsedThreshold >= 0.0
        && parsedThreshold <= 100.0
            ? parsedThreshold
            : ComfortBudgetReadService.DefaultThreshold;
    var windowDays = int.TryParse(
        cfg["COMFORT_BUDGET_WINDOW_DAYS"],
        System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture,
        out var parsedWindow)
        && parsedWindow > 0
        && parsedWindow <= 366
            ? parsedWindow
            : ComfortBudgetReadService.DefaultWindowDays;
    return new ComfortBudgetReadService(
        fetcher.FetchAsync,
        threshold: threshold,
        windowDays: windowDays);
});

// ---------------------------------------------------------------------
// Anomalies — slice 8. `AnomalyReadService` follows the same
// delegate-seam pattern. Both reads go through
// `dbo.fv_anomalies_at_cursor(@asOf)` so cursor-clipping is enforced
// by the schema. `dbo.Anomalies` is populated by the ml-tier's
// three-detector pipeline (SensorFailureRules + ResidualOutlierDetector
// + ChangepointDetector); the read path bypasses the ml container.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<SqlAnomalyFetcher>();
builder.Services.AddScoped<AnomalyReadService>(sp =>
{
    var fetcher = sp.GetRequiredService<SqlAnomalyFetcher>();
    return new AnomalyReadService(
        fetcher.FetchLatestAsync,
        fetcher.FetchRangeAsync);
});

// ---------------------------------------------------------------------
// Profiles — slice 9. `ProfileReadService` follows the same
// delegate-seam pattern. The read goes through
// `dbo.fv_dayprofiles_at_cursor(@asOf)` so cursor-clipping is enforced
// by the schema. `dbo.DayProfiles` is populated by the ml-tier's
// `ProfileEmitter` (nightly cron at 03:00 UTC + on-demand
// `POST /api/profiles/analyze`); the read path bypasses the ml
// container.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<SqlProfileFetcher>();
builder.Services.AddScoped<ProfileReadService>(sp =>
{
    var fetcher = sp.GetRequiredService<SqlProfileFetcher>();
    return new ProfileReadService(fetcher.FetchRangeAsync);
});

// ---------------------------------------------------------------------
// Alerts — slice 11. Three concrete services follow the same
// delegate-seam pattern used by slices 3-10:
//
//   * `AlertReadService`     — `GET /api/alerts` history read; clipped
//     via `dbo.fv_alerts_at_cursor(@asOf)` (init-db.sql §3.5).
//   * `AlertRuleReadService` — `GET /api/alerts/rules` list of enabled
//     rules.
//   * `AlertScanService`     — gaps-and-islands SQL runner; consumed by
//     the `ThresholdAlertScanner` hosted service which fires every
//     wall-minute and writes new `dbo.Alerts` rows + broadcasts a
//     `breach-detected` SSE event per insert.
//
// The SQL is on `SqlAlertScanner` (loadRules / scanBreaches / insert)
// and `SqlAlertReader` (history fetch) — golden-string pinned via
// public consts so the unit tests can lock the cursor-clipping +
// closure-only filter shape.
// ---------------------------------------------------------------------
builder.Services.AddSingleton<SqlAlertReader>();
builder.Services.AddSingleton<SqlAlertScanner>();

builder.Services.AddScoped<AlertReadService>(sp =>
{
    var reader = sp.GetRequiredService<SqlAlertReader>();
    return new AlertReadService(reader.FetchHistoryAsync);
});

builder.Services.AddScoped<AlertRuleReadService>(sp =>
{
    var scanner = sp.GetRequiredService<SqlAlertScanner>();
    return new AlertRuleReadService(scanner.LoadRulesAsync);
});

builder.Services.AddScoped<AlertScanService>(sp =>
{
    var scanner = sp.GetRequiredService<SqlAlertScanner>();
    return new AlertScanService(
        rulesLoader: scanner.LoadRulesAsync,
        breachScanner: scanner.ScanBreachesAsync,
        alertInserter: scanner.InsertAlertAsync);
});

// The initial-delay parameter is read once at registration so an
// operator can shorten it via `CLIMASENSE_ALERT_SCAN_INITIAL_DELAY_SECONDS`
// when bootstrapping a fresh demo. Default is 30 wall-seconds.
builder.Services.AddSingleton<ThresholdAlertScanner>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var initialDelay = ThresholdAlertScanner.DefaultInitialDelay;
    if (int.TryParse(
            cfg["CLIMASENSE_ALERT_SCAN_INITIAL_DELAY_SECONDS"],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedSeconds)
        && parsedSeconds >= 0
        && parsedSeconds <= 3600)
    {
        initialDelay = TimeSpan.FromSeconds(parsedSeconds);
    }
    return new ThresholdAlertScanner(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        stream: sp.GetRequiredService<AlertStream>(),
        clock: sp.GetRequiredService<IClock>(),
        logger: sp.GetRequiredService<ILogger<ThresholdAlertScanner>>(),
        initialDelay: initialDelay);
});
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<ThresholdAlertScanner>());

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

// ---------------------------------------------------------------------
// Readings — slice 3. Read-path bypass: this endpoint reads SQL Server
// directly via `SensorDataService` and never crosses into the ml tier.
// Returns 200 with the cursor-clipped latest row, or 404 if the
// table is empty (which only happens before bootstrap completes — the
// ml-tier readiness gate prevents the web tier from starting in that
// window).
// ---------------------------------------------------------------------
app.MapGet("/api/readings/latest", async (
    HttpContext ctx,
    SensorDataService sensors,
    CursorSnapshot cursor,
    CancellationToken cancellationToken) =>
{
    var latest = await sensors.GetLatestAsync(cursor, cancellationToken)
        .ConfigureAwait(false);
    if (latest is null)
    {
        return Results.Json(
            new
            {
                error = "no_readings_yet",
                message =
                    "SensorReadings is empty (bootstrap may still be running). " +
                    "Wait for /api/health/ready on the ml tier to return 200.",
                requestId = RequestIdMiddleware.Get(ctx),
            },
            statusCode: StatusCodes.Status404NotFound);
    }
    return Results.Json(latest, statusCode: StatusCodes.Status200OK);
});

// ---------------------------------------------------------------------
// Readings — slice 4. /range + /heatmap. Both bypass the ml tier and
// are cursor-clipped at the service layer (see RangeQueryService).
// ---------------------------------------------------------------------
app.MapGet("/api/readings/range", async (
    HttpContext ctx,
    RangeQueryService rangeQueries,
    CursorSnapshot cursor,
    string? start,
    string? end,
    string? bucket,
    CancellationToken cancellationToken) =>
{
    if (!TryParseUtc(start, out var startUtc))
    {
        return BadRequest(ctx,
            error: "invalid_start",
            message: "`start` must be an ISO 8601 UTC timestamp.");
    }
    if (!TryParseUtc(end, out var endUtc))
    {
        return BadRequest(ctx,
            error: "invalid_end",
            message: "`end` must be an ISO 8601 UTC timestamp.");
    }

    var bucketLiteral = string.IsNullOrWhiteSpace(bucket) ? "hour" : bucket;
    if (!RangeBucketExtensions.TryParseWire(bucketLiteral, out var bucketEnum))
    {
        return BadRequest(ctx,
            error: "invalid_bucket",
            message: "`bucket` must be one of: raw, hour, day, week.");
    }

    var args = new RangeQueryArgs(startUtc, endUtc, bucketEnum);
    var validation = rangeQueries.ValidateAndClip(cursor, args, out _, out _);
    switch (validation)
    {
        case RangeQueryError.StartAfterEnd:
            return BadRequest(ctx,
                error: "start_after_end",
                message: "`start` must be on or before `end`.");
        case RangeQueryError.RawWindowTooLarge:
            return BadRequest(ctx,
                error: "range_too_large",
                message:
                    "`raw` requests are capped at "
                    + rangeQueries.RawMaxDays
                    + " days. Use bucket=hour, day, or week for wider windows.");
    }

    var response = await rangeQueries
        .GetRangeAsync(cursor, args, cancellationToken)
        .ConfigureAwait(false);
    return Results.Json(response, statusCode: StatusCodes.Status200OK);
});

app.MapGet("/api/readings/heatmap", async (
    HttpContext ctx,
    RangeQueryService rangeQueries,
    CursorSnapshot cursor,
    int? year,
    CancellationToken cancellationToken) =>
{
    if (year is null)
    {
        return BadRequest(ctx,
            error: "missing_year",
            message: "Query parameter `year` is required.");
    }
    if (year is < 1900 or > 2100)
    {
        return BadRequest(ctx,
            error: "invalid_year",
            message: "`year` must be between 1900 and 2100.");
    }

    var response = await rangeQueries
        .GetHeatmapAsync(cursor, year.Value, cancellationToken)
        .ConfigureAwait(false);
    return Results.Json(response, statusCode: StatusCodes.Status200OK);
});

// ---------------------------------------------------------------------
// Forecasts — slice 5. Read-path bypass via the
// `dbo.fv_forecasts_at_cursor(@asOf)` inline TVF. Empty `points` is
// a valid 200 response — happens before the first emission lands.
// ---------------------------------------------------------------------
app.MapGet("/api/forecasts/latest", async (
    HttpContext ctx,
    ForecastReadService forecasts,
    CursorSnapshot cursor,
    CancellationToken cancellationToken) =>
{
    var envelope = await forecasts.GetLatestAsync(cursor, cancellationToken)
        .ConfigureAwait(false);
    return Results.Json(envelope, statusCode: StatusCodes.Status200OK);
});

// ---------------------------------------------------------------------
// Leaderboard — slice 6. Read-path bypass: the web tier executes
// `SELECT ... FROM dbo.Leaderboard ORDER BY Mae ASC` directly and
// never crosses into the ml container. The table is populated at
// FastAPI startup by `LeaderboardSeeder`. Empty `rows` is a valid
// 200 response (happens during the brief lifespan window before the
// seeder finishes).
// ---------------------------------------------------------------------
app.MapGet("/api/leaderboard", async (
    HttpContext ctx,
    LeaderboardReadService leaderboard,
    CancellationToken cancellationToken) =>
{
    var response = await leaderboard.GetAllAsync(cancellationToken)
        .ConfigureAwait(false);
    return Results.Json(response, statusCode: StatusCodes.Status200OK);
});

// ---------------------------------------------------------------------
// Comfort — slice 7. Read-path bypass: the web tier reads the most
// recent `dbo.ComfortScores` row through the
// `dbo.fv_comfortscores_at_cursor(@asOf)` TVF directly. The ml
// container is NOT involved. The table is populated by the ml-tier
// comfort scheduler (β-prime, one row per replay-hour) plus the
// on-demand `GET /api/comfort/score`. Returns 404 with a
// ProblemDetails-shaped body when no comfort row exists at or before
// the cursor.
// ---------------------------------------------------------------------
app.MapGet("/api/comfort/current", async (
    HttpContext ctx,
    ComfortReadService comfort,
    CursorSnapshot cursor,
    CancellationToken cancellationToken) =>
{
    var current = await comfort.GetCurrentAsync(cursor, cancellationToken)
        .ConfigureAwait(false);
    if (current is null)
    {
        return Results.Json(
            new
            {
                error = "no_comfort_yet",
                message =
                    "ComfortScores is empty at the cursor (the scheduler " +
                    "may not have emitted its first row yet). The on-demand " +
                    "GET /api/comfort/score endpoint also writes a row.",
                requestId = RequestIdMiddleware.Get(ctx),
            },
            statusCode: StatusCodes.Status404NotFound);
    }
    return Results.Json(current, statusCode: StatusCodes.Status200OK);
});

// ---------------------------------------------------------------------
// Comfort Budget — slice 10. Read-path bypass: three pure SQL
// aggregations served directly from `dbo.ComfortScores` +
// `dbo.DayProfiles` through the cursor-clipped TVFs
// (`dbo.fv_comfortscores_at_cursor`, `dbo.fv_dayprofiles_at_cursor`).
// The ml container is NOT involved. Empty windows return 200 with
// zero hours / null worstCell / empty trend (common during the brief
// lifespan window before the schedulers emit their first rows).
// ---------------------------------------------------------------------
app.MapGet("/api/comfort/budget", async (
    HttpContext ctx,
    ComfortBudgetReadService budget,
    CursorSnapshot cursor,
    CancellationToken cancellationToken) =>
{
    var response = await budget.GetAsync(cursor, cancellationToken)
        .ConfigureAwait(false);
    return Results.Json(response, statusCode: StatusCodes.Status200OK);
});

// ---------------------------------------------------------------------
// Anomalies — slice 8. Read-path bypass: web tier reads
// `dbo.Anomalies` through `dbo.fv_anomalies_at_cursor(@asOf)` directly;
// the ml container is NOT involved. The table is populated by the
// ml-tier three-detector pipeline (nightly scheduler + on-demand POST).
//   * /latest — most recent anomaly visible at the cursor; 404 when
//     none exist.
//   * /api/anomalies — range query with optional ?type= filter.
// ---------------------------------------------------------------------
app.MapGet("/api/anomalies/latest", async (
    HttpContext ctx,
    AnomalyReadService anomalies,
    CursorSnapshot cursor,
    CancellationToken cancellationToken) =>
{
    var latest = await anomalies.GetLatestAsync(cursor, cancellationToken)
        .ConfigureAwait(false);
    if (latest is null)
    {
        return Results.Json(
            new
            {
                error = "no_anomaly_yet",
                message =
                    "Anomalies is empty at the cursor (the nightly " +
                    "scheduler may not have fired yet). Trigger the " +
                    "on-demand POST /api/ml/run/anomalies to backfill.",
                requestId = RequestIdMiddleware.Get(ctx),
            },
            statusCode: StatusCodes.Status404NotFound);
    }
    return Results.Json(latest, statusCode: StatusCodes.Status200OK);
});

app.MapGet("/api/anomalies", async (
    HttpContext ctx,
    AnomalyReadService anomalies,
    CursorSnapshot cursor,
    string? start,
    string? end,
    string? type,
    CancellationToken cancellationToken) =>
{
    DateTime? startUtc = null;
    DateTime? endUtc = null;
    if (!string.IsNullOrWhiteSpace(start))
    {
        if (!TryParseUtc(start, out var parsed))
        {
            return BadRequest(ctx,
                error: "invalid_start",
                message: "`start` must be an ISO 8601 UTC timestamp.");
        }
        startUtc = parsed;
    }
    if (!string.IsNullOrWhiteSpace(end))
    {
        if (!TryParseUtc(end, out var parsed))
        {
            return BadRequest(ctx,
                error: "invalid_end",
                message: "`end` must be an ISO 8601 UTC timestamp.");
        }
        endUtc = parsed;
    }
    if (!string.IsNullOrWhiteSpace(type)
        && type is not "sensor_failure"
            and not "regime_shift"
            and not "residual_outlier")
    {
        return BadRequest(ctx,
            error: "invalid_type",
            message:
                "`type` must be one of: sensor_failure, regime_shift, residual_outlier.");
    }

    try
    {
        var response = await anomalies
            .GetRangeAsync(cursor, startUtc, endUtc, type, cancellationToken)
            .ConfigureAwait(false);
        return Results.Json(response, statusCode: StatusCodes.Status200OK);
    }
    catch (ArgumentException ex)
    {
        return BadRequest(ctx,
            error: "invalid_range",
            message: ex.Message);
    }
});

// ---------------------------------------------------------------------
// Profiles — slice 9. Read-path bypass: web tier reads
// `dbo.DayProfiles` through `dbo.fv_dayprofiles_at_cursor(@asOf)`
// directly; the ml container is NOT involved. The table is
// populated by the ml-tier's `ProfileEmitter` (nightly scheduler
// + on-demand `POST /api/profiles/analyze`).
//
// /api/profiles?start=YYYY-MM-DD&end=YYYY-MM-DD — defaults to
// "last 30 days at the cursor" when both are omitted. start > end
// or span > 366 days returns 400.
// ---------------------------------------------------------------------
app.MapGet("/api/profiles", async (
    HttpContext ctx,
    ProfileReadService profiles,
    CursorSnapshot cursor,
    string? start,
    string? end,
    CancellationToken cancellationToken) =>
{
    DateOnly? startDate = null;
    DateOnly? endDate = null;
    if (!string.IsNullOrWhiteSpace(start))
    {
        if (!DateOnly.TryParseExact(
                start,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            return BadRequest(ctx,
                error: "invalid_start",
                message: "`start` must be an ISO 8601 date (YYYY-MM-DD).");
        }
        startDate = parsed;
    }
    if (!string.IsNullOrWhiteSpace(end))
    {
        if (!DateOnly.TryParseExact(
                end,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            return BadRequest(ctx,
                error: "invalid_end",
                message: "`end` must be an ISO 8601 date (YYYY-MM-DD).");
        }
        endDate = parsed;
    }

    try
    {
        var response = await profiles
            .GetRangeAsync(cursor, startDate, endDate, cancellationToken)
            .ConfigureAwait(false);
        return Results.Json(response, statusCode: StatusCodes.Status200OK);
    }
    catch (ArgumentException ex)
    {
        return BadRequest(ctx,
            error: "invalid_range",
            message: ex.Message);
    }
});

// ---------------------------------------------------------------------
// Alerts — slice 11. Read-path bypass: web tier reads `dbo.Alerts`
// through `dbo.fv_alerts_at_cursor(@asOf)` directly; the ml
// container is NOT involved. The table is populated by the
// `ThresholdAlertScanner` BackgroundService (wall-minute cadence)
// running inside this same web tier.
//
//   * GET /api/alerts?limit=N    — most-recent alerts (clamped 1..200).
//   * GET /api/alerts/rules      — list of enabled rules.
//
// The SSE `breach-detected` event uses the existing `/api/alerts/stream`
// endpoint (slice 1) — see `AlertStream.Broadcast` invoked from
// `ThresholdAlertScanner.TickOnceAsync`.
// ---------------------------------------------------------------------
app.MapGet("/api/alerts", async (
    HttpContext ctx,
    AlertReadService alerts,
    CursorSnapshot cursor,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (limit is { } l && (l < 1 || l > AlertReadService.MaxLimit))
    {
        return BadRequest(ctx,
            error: "invalid_limit",
            message:
                "`limit` must be between 1 and " + AlertReadService.MaxLimit + ".");
    }

    var response = await alerts
        .GetHistoryAsync(cursor, limit, cancellationToken)
        .ConfigureAwait(false);
    return Results.Json(response, statusCode: StatusCodes.Status200OK);
});

app.MapGet("/api/alerts/rules", async (
    HttpContext ctx,
    AlertRuleReadService rules,
    CancellationToken cancellationToken) =>
{
    var response = await rules
        .GetAllAsync(cancellationToken)
        .ConfigureAwait(false);
    return Results.Json(response, statusCode: StatusCodes.Status200OK);
});

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

// ---------------------------------------------------------------------
// Slice-4 helpers — ISO 8601 parsing for query-string timestamps and a
// uniform 400 response shape. The 400 body mirrors the slice-3 404 body
// (error / message / requestId) so the dashboard handler has one
// rendering path for "bad request" outcomes.
// ---------------------------------------------------------------------
static bool TryParseUtc(string? raw, out DateTime utc)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        utc = default;
        return false;
    }

    if (DateTime.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed))
    {
        utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
    utc = default;
    return false;
}

static IResult BadRequest(HttpContext ctx, string error, string message)
{
    return Results.Json(
        new
        {
            error,
            message,
            requestId = RequestIdMiddleware.Get(ctx),
        },
        statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>
/// Public marker class so xUnit's <c>WebApplicationFactory&lt;TEntryPoint&gt;</c>
/// can locate the test host. Sits at the root namespace deliberately —
/// the integration tests construct it as <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program { }
