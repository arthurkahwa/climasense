# Slice 3 implementation notes (bcp bootstrap + latest-reading widget)

> Auxiliary working-notes file for slice 3. Captures the reviewer-facing
> map of what landed and how to validate it. See the PR body for the
> full acceptance-criteria evidence table.

## What landed

| Concern | Path | Notes |
|---|---|---|
| Contract extension | `contracts/openapi.yaml` | New path `/api/readings/latest` + `LatestReading` schema (camelCase: `readingTime`, `temperatureC`, `humidityPct`). Bumped `info.version` to `0.3.0-slice-3`. |
| Validator exclusion | `src/ClimaSense.ML/climasense_ml/contract_validator.py` | `/api/readings/latest` joined `/api/alerts/stream` in `_ML_TIER_EXCLUDED_PATHS`; `LatestReading` joined `_DROP_SCHEMAS` since it's only referenced by the web-tier-only path. |
| Codegen — .NET | `src/ClimaSense.Web/Generated/MLClient/` | Kiota regenerated. New `Models/LatestReading.cs` + `Api/Readings/` request-builder tree. |
| Codegen — Python | `src/ClimaSense.ML/climasense_ml/schemas/generated.py` | `datamodel-codegen` regenerated. `LatestReading` class added to the package surface. |
| Schemas re-export | `src/ClimaSense.ML/climasense_ml/schemas/__init__.py` | Added `LatestReading` to the re-export tuple. |
| Ingestion service | `src/ClimaSense.ML/climasense_ml/ingestion.py` | `IngestionService` (concrete class, no `IIngestionService`) with `bootstrap_from_csv_if_empty()` (production) and `pull_increment()` (stub raising `NotImplementedError`). `transform_to_seed_csv()` is a free function — the pandas pipeline. |
| bcp format file | embedded in `ingestion.py` | Non-XML format file mapping 3 CSV columns to SQL columns 2/3/4 (skipping the `Id` IDENTITY column). Written next to the seed CSV before bcp runs. |
| Lifespan wiring | `src/ClimaSense.ML/climasense_ml/main.py` | Imports `IngestionService`; adds `_BootstrapTracker` (thread-safe singleton state machine); fires `_run_bootstrap_blocking` via `asyncio.to_thread` on lifespan start. Readiness probe now reads `bootstrap` state. |
| Readiness gate | `src/ClimaSense.ML/climasense_ml/main.py` | `_probe_ready()` composes DB + bootstrap checks. Returns 503 with `bootstrap=skipped/fail` until bootstrap reaches `complete` or `skipped`. |
| ml Dockerfile | `src/ClimaSense.ML/Dockerfile` | Adds `mssql-tools18` to the apt install (provides `/opt/mssql-tools18/bin/bcp`); prepends that directory to `PATH`. Adds `pandas` to the pip install layer. |
| Python pyproject | `src/ClimaSense.ML/pyproject.toml` | Adds `pandas>=2.2,<4`. Bumps version to `0.3.0`. |
| .NET readings | `src/ClimaSense.Web/Readings/` | New folder with three files: `LatestReading.cs` (record DTO), `SensorDataService.cs` (concrete service + `LatestReadingFetcher` delegate seam), `SqlLatestReadingFetcher.cs` (production SQL adapter). |
| Endpoint mapping | `src/ClimaSense.Web/Program.cs` | `MapGet("/api/readings/latest", ...)` reads from `SensorDataService` (scoped) with `CursorSnapshot` (scoped) — cursor-clipped query. 404 with `no_readings_yet` when the table is empty (bootstrap incomplete). |
| Dashboard | `src/ClimaSense.Web/Pages/Index.cshtml` | Renamed from slice-1 placeholder. Adds the "Latest reading" card (temperature + humidity + timestamp) above the heartbeat counter. Hydrated on page load via a single `fetch('/api/readings/latest')`. No SSE for this widget — auto-refresh lands with the replay clock. |
| Compose | `docker-compose.yml` | Bind-mounts `./sensor_data.csv` → `/data/sensor_data.csv:ro` in the `ml` container; sets `CLIMASENSE_BOOTSTRAP_CSV` + `CLIMASENSE_SEED_CSV` env. The `start_period: 300s` healthcheck from slice 1 already covered the 30-90 s bootstrap window. |
| Python tests | `src/ClimaSense.ML/tests/test_ingestion.py` (10) + `tests/test_bootstrap_state.py` (6) | 16 new tests. Total Python tests: **45 passing** (was 29 in slice 2). |
| .NET tests | `tests/ClimaSense.Web.Tests/SensorDataServiceTests.cs` (6) + `tests/.../LatestReadingEndpointTests.cs` (4) | 10 new tests. Total .NET tests: **46 passing** (was 36 in slice 2). Adds `Microsoft.AspNetCore.Mvc.Testing` to the test project for `WebApplicationFactory<Program>` integration tests. |
| Notes | `SLICE-3-NOTES.md` | this file |
| ADR | `docs/adr/0013-bcp-bootstrap-strategy.md` | Pins the bcp + pandas approach with the format-file column-mapping decision. |

## Verification (the same commands captured in the PR body)

```sh
# Python tests
python3 -m pytest src/ClimaSense.ML/tests/ -v                       # 45 passed

# .NET tests
dotnet test -nologo -p:RegenerateKiotaClient=false                  # 46 passed

# Cold-boot compose lifecycle (drop volume first)
cp .env.example .env
docker compose down -v
docker compose up -d
# ~35 s later: ml flips to healthy, web shortly after.

docker compose ps                                                   # all 4 healthy
docker compose logs ml | grep "Bootstrap: complete"                 # 1 hit
# → "Bootstrap: complete (2450920 deduped, 3065553 raw)"

# Latest reading endpoint
curl -fsS http://127.0.0.1:8080/api/readings/latest
# → {"readingTime":"2026-05-07T16:15:02.27Z","temperatureC":19,"humidityPct":41}

# X-Request-ID still propagates
curl -sS -H 'X-Request-ID: probe-slice3' -D - http://127.0.0.1:8080/api/readings/latest | head -10

# Row count in SQL
docker exec climasense-db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -No -d ClimaSense \
  -Q "SELECT COUNT_BIG(*) FROM dbo.SensorReadings;"
# → 2450920

# Idempotent re-boot (keep volume)
docker compose restart ml
docker compose logs ml --tail 10 | grep "Bootstrap"
# → "Bootstrap: skipped (table non-empty, probe 1)"

# Dashboard renders the latest-reading widget alongside the slice-1 heartbeat
curl -s http://127.0.0.1:8080/ | grep -E '(latest-card|tick-count|server-tick|temperatureC)'

# Slice-2 proxy still maps stubs to 501 + ProblemDetails
curl -sS -i http://127.0.0.1:8080/api/ml/forecast | head -10

# Reset
docker compose down -v
```

## What was deliberately NOT built (deferred to later slices)

* **Range / heatmap reads** — slice 4 (#6) ships `GET /api/readings/range?start&end&bucket=hour|day|week` and `/api/readings/heatmap?year=YYYY`. EF Core scaffold also lands in slice 4 — slice 3 deliberately uses raw `Microsoft.Data.SqlClient` for the single `SELECT TOP 1` to keep the slice diff small.
* **`pull_increment` scheduling** — the method raises `NotImplementedError`; APScheduler registration belongs to a future WallClock-only slice (post-slice-12).
* **Auto-refresh dashboard** — slice 12 (#14) wires the cursor's `clock-changed` SSE event to a browser handler that re-fetches `/api/readings/latest`. Slice 3 fetches once on page load.
* **CSV in CI** — `sensor_data.csv` is gitignored; CI will mock the bootstrap by setting `CLIMASENSE_SKIP_BOOTSTRAP=1` (already wired) once CI lands in slice 14 (#16).
* **Two-tier readiness signals** — slice 3 collapses "bootstrap complete" into the same `/api/health/ready` endpoint as DB reachability. A future ADR may split it.

## Judgment calls

1. **Format-file column mapping vs schema change.** The slice-3 brief envisaged a straight `bcp ... -c -t ,` invocation, but `SensorReadings.Id` is `BIGINT IDENTITY` and the seed CSV omits it. `bcp -c -t ,` then tries to insert the CSV's first column (a timestamp string) into the `Id` BIGINT column — which fails with SQL state 22005. The clean fix is a non-XML format file mapping CSV columns 1/2/3 → SQL columns 2/3/4 (skipping `Id`). The format file is written by `IngestionService` next to the seed CSV; the `-c`/`-t` flags are replaced by `-f <fmt>`. The argv-shape test (`test_bcp_invocation_matches_expected_argv_shape`) was updated to pin the new shape. An alternative — change `Id` to non-IDENTITY — was rejected because it would diverge from the slice-1 schema authority + ADR.

2. **`SensorDataService` takes a delegate, not an `ISensorReadingsReader` interface.** ADR-0011's "interface emergence policy" rules out speculative interfaces. The only second adapter is the test fake — which is a lambda, not a class. So the seam is parameterised as a `LatestReadingFetcher` delegate (Func-shaped). When slice 4 adds `/range` and `/heatmap` with their own fetcher signatures, the seam might cohere into a single interface — but that's *informed* by two concrete shapes, not speculated from one.

3. **Bootstrap runs as a background task during lifespan, not a blocking call.** The slice-3 brief said "bootstrap completes before readiness flips." The deployment-contract interpretation is "the healthcheck reports bootstrap state correctly" — and a non-blocking lifespan keeps `/api/health/live` responsive even mid-bootstrap, which matches the slice-1 contract for the liveness probe. The readiness probe gates on `_BootstrapTracker.state in {complete, skipped}`, which is the proper signal for the dependent `web` service.

4. **404 (not 204) when `SensorReadings` is empty.** A 204 No Content is technically more accurate for an empty collection, but `/api/readings/latest` returns *a single resource* — and a single-resource read returning 404 on "no such resource" is the conventional HTTP semantic. The body's `error: no_readings_yet` lets the dashboard JavaScript surface a meaningful message rather than silently degrading.

5. **`temperatureC` / `humidityPct` wire names (unit-tagged).** The slice-3 acceptance criterion pins these exact spellings. The PRD's wider conversation also used `temperature` / `humidity` in places — the AC wins. Carrying the unit in the field name keeps the dashboard JavaScript honest (it can't render "21.5" with no unit).

6. **bcp's `-u` flag for self-signed cert trust.** mssql-tools18's bcp defaults to encrypted connections with strict cert validation. The dev compose stack uses SQL Server's self-signed certificate; without `-u`, bcp fails with "SSL Provider: error". `-u` mirrors slice 1's sqlcmd `-No` posture. Production deployments would use a real CA-signed cert and drop the flag.

7. **`CLIMASENSE_SKIP_BOOTSTRAP=1` knob for tests.** TestClient invokes the lifespan and would otherwise try to read `/data/sensor_data.csv`. Adding the env knob keeps the slice-1/slice-2 tests fast and offline. Production never sets the knob; it's a test-only escape hatch documented in `_run_bootstrap_blocking`'s docstring.

## .NET 10 GA gotchas (NONE re-introduced)

* SDK / ASP.NET image tags: still `mcr.microsoft.com/dotnet/sdk:10.0` + `aspnet:10.0`. No `-preview` anywhere.
* `<InvariantGlobalization>false</InvariantGlobalization>` retained in `ClimaSense.Web.csproj`; `libicu-dev` + `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false` retained in the web Dockerfile.
* No `switch` cases differ only by nullable annotation — guard rail from slice 1 still holds.
* Uvicorn is not started with `--log-config /dev/null`; lifespan installs the JSON formatter as before.

## Compose regression checks (slice 1 + 2 still work)

* `docker compose up -d --wait` brings 4 containers to healthy in ~35-50 s after a fresh bootstrap.
* `curl http://127.0.0.1:8080/api/health/{live,ready}` returns 200 with the slice-1 body shape.
* `curl -N http://127.0.0.1:8080/api/alerts/stream` emits `event: server-tick` frames (slice 1 SSE heartbeat).
* `curl http://127.0.0.1:8080/api/ml/forecast` returns 501 with `{"error":"not_implemented", ...}` (slice 2 proxy + failure-mapping pipeline).
* `docker compose logs ml | grep "ContractValidator: OK"` shows the validator passing with `(7 paths, 18 schemas)` — one extra schema (`LatestReading`) compared to slice 2's 17, because the validator explicitly excludes web-tier-only schemas from the comparison.
