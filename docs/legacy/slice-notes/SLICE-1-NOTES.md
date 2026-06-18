# Slice 1 implementation notes (foundation)

> Auxiliary working-notes file for slice 1. Captures the reviewer-facing
> map of what landed and how to validate it. See PR #N body for the
> full acceptance-criteria evidence table.

## What landed

| Concern | Path | Notes |
|---|---|---|
| Compose stack | `docker-compose.yml` | `db` + `db-init` + `ml` + `web`. Only `web` publishes a host port (`127.0.0.1:8080`). |
| Schema authority | `scripts/init-db.sql` | 8 tables + 5 cursor TVFs + 1 pattern classifier + 3 seed alert rules. Idempotent. SQL-first; no migrations. |
| Cross-tier contract | `contracts/openapi.yaml` | Slice-1 minimal surface: health probes + SSE channel description. |
| `IClock` (.NET) | `src/ClimaSense.Web/Clock/{IClock,WallClock}.cs` | `WallClock` only; `ReplayClock` `TODO(slice-12)` at registration site. |
| `IClock` (Python) | `src/ClimaSense.ML/climasense_ml/clock.py` | Same shape, mirrored hand-written. |
| `CursorSnapshot` (.NET) | `src/ClimaSense.Web/Cursor/CursorSnapshot.cs` | Scoped DI service. Operations: `AsOf`, `Clip`, `Windowed`, `ShouldEmit`. |
| `CursorSnapshot` (Python) | `src/ClimaSense.ML/climasense_ml/cursor.py` | Bound via `contextvars` by `CursorScopeMiddleware`. Operations: `as_of`, `clip`, `windowed`, `should_emit`. |
| Structured JSON logs (.NET) | `src/ClimaSense.Web/Logging/JsonStdoutFormatter.cs` | Custom `ConsoleFormatter`; one JSON object per line; `request_id` from log scope. |
| Structured JSON logs (Python) | `src/ClimaSense.ML/climasense_ml/logging_setup.py` | `python-json-logger` subclass; `request_id` from contextvar. |
| `X-Request-ID` (.NET) | `src/ClimaSense.Web/Logging/RequestIdMiddleware.cs` | Mints / accepts; ASCII guard 1-128 chars; mirrors back; pushes to log scope. |
| `X-Request-ID` (Python) | `src/ClimaSense.ML/climasense_ml/main.py` (`RequestIdMiddleware`) | Same contract; binds via `contextvars`. |
| Health (`/api/health/{live,ready}`) | both tiers | Live = process up; Ready = DB connectivity probe (returns 503 until DB reachable). |
| SSE infrastructure | `src/ClimaSense.Web/Sse/{AlertStream,HeartbeatService}.cs` + `Program.cs` `/api/alerts/stream` | `AlertStream` singleton; per-subscriber bounded channel; `HeartbeatService` emits `server-tick` every 15 s. |
| Placeholder Razor page | `src/ClimaSense.Web/Pages/Index.cshtml{,.cs}` | Subscribes via `EventSource`, renders a heartbeat counter. |
| ADR | `docs/adr/0011-cursor-snapshot-and-interface-emergence-policy.md` | Pins the `CursorSnapshot` lifetime contract + the no-`IForecaster`/`IAnomalyStrategy` policy. |
| Tests (.NET) | `tests/ClimaSense.Web.Tests/CursorSnapshotTests.cs` | 14 xUnit tests — 3 acceptance criteria + guard rails. |
| Tests (Python) | `src/ClimaSense.ML/tests/test_cursor_snapshot.py` (14) + `test_request_id.py` (4) | All pass under `python -m pytest`. |

## What was deliberately NOT built (deferred to later slices)

* **Ingestion** — `bcp` loader + `IngestionService.bootstrap_from_csv_if_empty()` are slice 3 (#5). `init-db.sql` creates empty tables.
* **`ReplayClock`** — slice 12 (#14). Registration sites in both tiers carry `TODO(slice-12)` markers.
* **Five-test golden suite** — golden tests land per-feature in slices 7–11 (lag-LR boot fit, comfort polygons, breach SQL, anomaly idempotency).
* **E2E smoke test** — slice 13 (#15).
* **CI workflow** (`.github/workflows/`) — explicitly out of scope per task brief.
* **Anything from the .NET / FastAPI / SQL surface beyond what slice 1 touches** — controllers, repositories, EF Core scaffold, Razor pages other than Index, alert engine, comfort calculator, all four `POST /api/ml/run/*` endpoints.

## Local verification commands

These are the verification steps a reviewer runs to convince themselves
slice 1 works. They are also the commands captured in the PR body's
acceptance-criteria evidence table.

```bash
# YAML / SQL static checks
python3 -c "import yaml; yaml.safe_load(open('docker-compose.yml'))"
python3 -c "import yaml; yaml.safe_load(open('contracts/openapi.yaml'))"

# Python tests (CursorSnapshot + request-ID propagation)
python3 -m pytest src/ClimaSense.ML/tests/ -v

# Compose lifecycle (requires Docker)
docker compose up -d
docker compose ps                                          # all healthy
curl -fsS http://127.0.0.1:8080/api/health/live | jq .
curl -fsS http://127.0.0.1:8080/api/health/ready | jq .
curl --no-buffer -N http://127.0.0.1:8080/api/alerts/stream  # see server-tick events
curl -H 'X-Request-ID: probe-abc-123' http://127.0.0.1:8080/api/health/live
docker compose logs web | grep probe-abc-123               # request_id appears
docker compose down
```

## Pattern-threshold provenance (PR judgment call)

The `dbo.fn_classify_pattern` constants (1.281552 ≈ Φ⁻¹(0.90); 0.6745 ≈
Φ⁻¹(0.75)) are pinned defaults. The notebook (`Climate_Time_Series_Analysis.ipynb`)
characterises the signal in §5 (EDA — temperature σ ≈ 1.6 °C, hour×weekday
heatmaps) and §6 (TSA — stationarity tests, decompositions) but does
NOT compute per-cohort percentiles of `MaxAbsZscore` or `MeanResidual`
explicitly. The constants therefore carry the provisional citation
"from notebook EDA section §5" and a slice-10 follow-up note in
`init-db.sql` to replace them with empirical per-cohort percentiles
once `DayProfiles` rows accumulate.
