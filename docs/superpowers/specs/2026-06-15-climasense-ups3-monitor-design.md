# ClimaSense — UPS-Room Environment Monitor (.NET 10 / IIS) — Design Spec

- **Date:** 2026-06-15
- **Status:** Approved design — pre-implementation
- **Supersedes:** the containerized ClimaSense (docker-compose, `src/ClimaSense.ML` Python tier, Kiota ML client, replay clock, bundled SQL Server, multi-table `init-db.sql`). All of that is retired.

## 1. Purpose & context

ClimaSense is reimagined as a **read-only environment-monitoring dashboard for a UPS / equipment room**. It surfaces live and historical **temperature** and **humidity** from an existing SQL Server dataset, and flags when conditions leave the equipment-safe envelope or when the sensor feed goes stale.

Developed on macOS (.NET 10 / Kestrel); deployed to **IIS on Windows Server** (in-process, ASP.NET Core Module). It connects to a pre-existing MSSQL instance — no Docker, no bundled database, no ingestion, no writes.

## 2. Data source (ground truth)

- **Server:** `util02.lab.local,1433` (SQL Server, self-signed cert).
- **Database:** `ups3` · **Table:** `dbo.tbl_sensor_data`.

| column | type | notes |
|---|---|---|
| `id` | `bigint` | PK (nonclustered, unique), monotonic |
| `sensor_dateTime` | `datetime` | reading time — **CET wall-clock, DST-observed** |
| `temperature` | `int` | whole °C |
| `humidity` | `int` | whole % RH |

- **Volume / cadence:** ~3,069,300 rows; 2016-01-20 → present; **live**, appended at a **~15-minute cadence** (~96 rows/day).
- **Single zone:** no device/sensor id column → one logical sensor.
- **Indexing:** only the PK on `id`; `sensor_dateTime` is **unindexed** (see §6).

## 3. Scope

**In scope (this spec — first slice):** live status, trend charts, historical explorer with server-side aggregates + calendar heatmap, recent-excursions list, health endpoint.

**Out of scope (later specs, see §15):** configurable alerting + alert log + email, stuck-sensor / spike anomaly detection, forecasting/trend projection, multi-zone, any write-back or ingestion.

## 4. Authentication & security

- The app connects to `ups3` as SQL login **`<your-db-user>`** (password `<your-db-password>`), with `Encrypt=True;TrustServerCertificate=True` (util02's cert is self-signed).
- **Security note:** `<your-db-user>` is a **`sysadmin`**. That is far more privilege than a read-only dashboard needs; if the web tier is compromised, the login owns the whole server.
  - **Recommended hardening (tracked, non-blocking):** create a least-privilege login `climasense_ro` with `SELECT` on `dbo.tbl_sensor_data` only and switch the connection string. Script shipped as `scripts/climasense_ro.sql`.
- **Secret handling:** the connection string lives in env var **`CLIMASENSE_UPS3_CONNECTION`** in production and is never committed. Option: encrypt the `connectionStrings` web.config section via `aspnet_regiis` / DPAPI.

## 5. Architecture

- One project **`ClimaSense.Monitor`** (`Microsoft.NET.Sdk.Web`, `net10.0`).
- **Razor Pages** (HTML) + **Minimal-API** JSON endpoints + a thin **Dapper** data layer over `Microsoft.Data.SqlClient`.
- **Hosting:** Kestrel for dev (macOS); **IIS in-process** via the ASP.NET Core Module in prod. `dotnet publish -c Release` emits `web.config`. App pool = **"No Managed Code"**; the app-pool identity needs **no** DB rights (SQL auth is used).
- Pure domain logic is isolated from I/O so it is unit-testable without a database.

## 6. Data layer

`ISensorReadingRepository`:

- `GetLatestAsync()` → `TOP 1 ... ORDER BY sensor_dateTime DESC`.
- `GetSeriesAsync(from, to, bucketMinutes)` → bucketed `AVG/MIN/MAX` (query below).
- `GetDailyAggregatesAsync(from, to)` → `GROUP BY CAST(sensor_dateTime AS date)` for the heatmap.
- `GetExcursionsAsync(from, to)` → v1 derives contiguous out-of-band runs **in C#** from the hourly series; a SQL gaps-and-islands version is a later optimization.

**Downsampling query (the core read):**

```sql
SELECT
  DATEADD(MINUTE,(DATEDIFF(MINUTE,'2000-01-01',sensor_dateTime)/@bucket)*@bucket,'2000-01-01') AS bucketStart,
  AVG(CAST(temperature AS float)) avgTemp, MIN(temperature) minTemp, MAX(temperature) maxTemp,
  AVG(CAST(humidity   AS float)) avgHum,  MIN(humidity)   minHum,  MAX(humidity)   maxHum, COUNT(*) n
FROM dbo.tbl_sensor_data
WHERE sensor_dateTime >= @from AND sensor_dateTime < @to
GROUP BY DATEADD(MINUTE,(DATEDIFF(MINUTE,'2000-01-01',sensor_dateTime)/@bucket)*@bucket,'2000-01-01')
ORDER BY bucketStart;
```

**Bucket auto-selection** (bounds point counts): `≤2 d → 15 min` · `≤14 d → 60 min` · `≤90 d → 360 min` · `else → 1440 min`.

**Performance** — `sensor_dateTime` is unindexed, so range queries scan 3 M rows:
- (a) **Approved:** add nonclustered index `IX_tbl_sensor_data_sensor_dateTime` on `sensor_dateTime` (`scripts/ups3-index.sql`, run by <your-db-user>).
- (b) **`IMemoryCache`** on aggregates — latest ~30 s TTL, historical buckets ~5 min TTL. The app is correct without the index; cache + index make it fast.

All queries parameterized; `CommandTimeout` ~15 s; connection pooling on; read-only.

## 7. Domain model & rules (pure, unit-tested)

- `record SensorReading(long Id, DateTime Timestamp, int TemperatureC, int HumidityPct)` — `sensor_dateTime` maps to `Timestamp` (no "At"-suffixed names per repo rule).
- `EnvelopeOptions` (config-bound), defaults from ASHRAE TC 9.9 data-center guidance, all tunable:
  - Temperature — Recommended **18–27 °C**, Allowable **15–32 °C**.
  - Humidity — Recommended **20–80 % RH**, Allowable **8–90 % RH**.
  - `FreshnessMinutes` = **30** (two missed 15-minute intervals).
- `ReadingBand` per metric: `Recommended | Allowable | OutOfRange`; overall status = worst of temp/humidity (drives tile color).
- **Freshness:** stale when `nowUtc − toUtc(Timestamp) > FreshnessMinutes` (see §8).
- `Excursion(Metric, StartCet, EndCet, DurationMinutes, PeakValue, Band)`.

## 8. Time zone

- `sensor_dateTime` is **CET wall-clock with DST**. The app uses IANA **`Europe/Berlin`** via `TimeZoneInfo` (resolves on macOS/Linux and Windows under .NET 10) for every conversion.
- Freshness compares `TimeZoneInfo.ConvertTimeToUtc(reading.Timestamp, cet)` to `DateTime.UtcNow`.
- **Query bounds (`@from`/`@to`) are CET wall-clock** — the same basis as `sensor_dateTime` — so range filtering applies no conversion; only "now"/freshness crosses zones. The endpoint layer parses user-supplied range inputs as CET.
- All display and chart axes render in CET. The once-yearly DST fall-back ambiguous hour is accepted (negligible for monitoring).

## 9. Pages / UX

Dark theme, responsive, **Chart.js**, vanilla JS (no SPA framework).

- **Live (`/`):** large temperature & humidity tiles colored by band; **"updated N min ago"** with a **stale-feed banner** when older than `FreshnessMinutes`; a 24-hour dual trend chart. JS polls `/api/readings/latest` and `/api/readings/series?range=24h` every 60 s.
- **History (`/history`):** range presets (24 h / 7 d / 30 d / 90 d / 1 y / custom) → temperature & humidity line charts with min/max bands; a **calendar heatmap** (day cells by avg temp or % time out-of-band); an **excursions table** (metric, start, end, duration, peak).

## 10. Endpoints (Minimal API)

- `GET /api/readings/latest` → `{ reading, band, freshness }`.
- `GET /api/readings/series?from&to&bucket` (or `?range=`) → bucketed points.
- `GET /api/readings/daily?from&to` → daily aggregates.
- `GET /api/readings/excursions?from&to` → excursions.
- `GET /health` → DB connectivity + feed freshness (degraded when stale).

Inputs validated: parseable dates, `from < to`, max-range cap (2 years), bucket whitelist.

## 11. Error handling

- DB unreachable / timeout → structured error JSON (502/503) + UI "data unavailable" (no crash); `/health` degraded; logged via `ILogger`.
- Stale feed → UI warning + `/health` degraded (not an error).
- Empty range → empty series rendered cleanly.

## 12. Testing

- **Unit (xUnit, pure):** band classification, freshness, bucket selection, excursion detection, input validation, CET↔UTC conversion.
- **Integration (opt-in, needs `ups3`):** repository query shape + aggregation correctness over known windows; trait-gated so default `dotnet test` needs no DB.
- **Smoke:** endpoints return 200 + valid JSON shape.

## 13. Project structure

```
ClimaSense.Monitor/
  Program.cs
  appsettings.json · appsettings.Production.json
  Domain/    SensorReading.cs  EnvelopeOptions.cs  ReadingBand.cs  Aggregation.cs  Excursion.cs  CetClock.cs
  Data/      ISensorReadingRepository.cs  SqlSensorReadingRepository.cs
  Endpoints/ ReadingsApi.cs
  Pages/     Index.cshtml(.cs)  History.cshtml(.cs)  Shared/_Layout.cshtml
  wwwroot/   css/site.css  js/{live,history,charts}.js
ClimaSense.Monitor.Tests/   (Domain unit tests + opt-in integration)
scripts/     ups3-index.sql   climasense_ro.sql
```

## 14. Deployment (IIS)

1. Install the **ASP.NET Core 10 Hosting Bundle** on the Windows server (ANCM + runtime).
2. From the Mac: `dotnet publish -c Release -o ./publish`; copy `./publish` to the IIS site folder.
3. IIS site → app pool **"No Managed Code"**, in-process; add an HTTPS binding.
4. Set machine/site env var `CLIMASENSE_UPS3_CONNECTION` (or use an encrypted web.config section).
5. Optional: run `scripts/ups3-index.sql` (perf) and `scripts/climasense_ro.sql` (least-privilege login).

## 15. Slice roadmap (future specs)

2. Alerting — excursion + heartbeat log, optional email.
3. Anomaly detection — stuck-sensor / spike.
4. Forecasting — short-horizon trend projection.
