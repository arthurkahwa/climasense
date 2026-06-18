# Slice 8 implementation notes (three-detector anomaly pipeline)

> Auxiliary working-notes for slice 8 (issue #10). Captures the
> reviewer-facing map of what landed and how to validate it.

## What landed

| Concern | Path | Notes |
|---|---|---|
| Sensor-failure rules | `src/ClimaSense.ML/climasense_ml/anomaly_sensor_failure.py` | `SensorFailureRules.scan_recent(snap)` — three SQL window-function INSERTs (gap > 10 min via `LAG(ReadingTime)`, stuck-value run >= 5 via gaps-and-islands, out-of-range via [T, RH] bounds). 24h scan window. Idempotency via `WHERE NOT EXISTS` against `UQ_Anomalies_TypeTime`. |
| Residual-outlier detector | `src/ClimaSense.ML/climasense_ml/anomaly_residual.py` | `ResidualOutlierDetector.scan_recent(snap)` — consumes the boot-fitted `LagLinearForecaster`. Per-hour `predict(history < t, 1h)`; severity = `|residual| / rolling σ` over the last `ROLLING_WINDOW_HOURS=48`. Z-threshold default 3.0. Same idempotency gate. |
| Changepoint detector | `src/ClimaSense.ML/climasense_ml/anomaly_changepoint.py` | `ChangepointDetector.rescan_window(snap, days=90)` — PELT via `ruptures.Pelt(model='rbf')` on the daily-mean temperature series. Transactional scan-and-replace inside `sp_getapplock @Resource='changepoint_scan'`. Penalty `pen=10.0` (judgment call — see PR). |
| Orchestrator | `src/ClimaSense.ML/climasense_ml/anomaly_orchestrator.py` | `run_all_detectors(snap, …)` — sequences the three; aggregates per-type counts into `AnomalyRunSummary`. `run_safely` swallows per-detector exceptions so a failure in one does NOT silence the other two. Per ADR-0011: no `IAnomalyStrategy` interface; the differing method names (`scan_recent` × 2, `rescan_window` × 1) are visible at the call site. |
| Persistence read | `src/ClimaSense.ML/climasense_ml/anomaly_persistence.py` | `read_recent_rows(engine, snap, since)` + `read_anomaly_counts_by_type` — both read through `dbo.fv_anomalies_at_cursor(@asOf)`. Cursor-clipping is a property of the schema. |
| Anomaly router | `src/ClimaSense.ML/climasense_ml/anomaly_router.py` | Real `POST /api/anomalies/detect` handler. Constructs the three detectors per-request from engine + boot-fitted forecaster; runs the orchestrator; reads back the rows via the persistence helper; returns `AnomalyDetectResponse` envelope with `perType: AnomalyRunSummary`. Returns 503 with `error: forecaster_not_ready` when the forecaster hasn't boot-fitted. |
| Lifespan wiring | `src/ClimaSense.ML/climasense_ml/main.py` | Imports + wires the anomaly router (BEFORE the stub router so `/api/anomalies/detect` is now real). New `_register_anomaly_scheduler(app)` — APScheduler `cron` job at 02:00 UTC fires `run_safely(snap, …)`. App version bumped to `0.8.0-slice-8`. |
| Stub cleanup | `src/ClimaSense.ML/climasense_ml/stubs.py` | Anomalies stub removed (handler is real now); `AnomalyDetectRequest` + `AnomalyDetectResponse` imports dropped. Module docstring updated. |
| Stub test | `src/ClimaSense.ML/tests/test_stubs_return_501.py` | `/api/anomalies/detect`, `/api/anomalies/latest`, `/api/anomalies` added to `_REAL_ENDPOINTS`; floor dropped from 2 to 1 (profiles remain). `CLIMASENSE_SKIP_ANOMALY_SCHEDULER=1` added to the test env. |
| Web read service | `src/ClimaSense.Web/Anomalies/` | `AnomalyReadService` + `SqlAnomalyFetcher` + `LatestAnomalyDto`/`AnomaliesResponseDto`. Mirrors slice-5/6/7 delegate-seam pattern. Two delegate seams (`LatestAnomalyFetcher`, `AnomalyRangeFetcher`); no `IAnomalyReadService` interface (ADR-0011). |
| Web endpoints | `src/ClimaSense.Web/Program.cs` | `GET /api/anomalies/latest` (404 with `error: no_anomaly_yet` when empty) + `GET /api/anomalies?start=&end=&type=` (range + type filter) registered alongside slice-3/4/5/6/7 reads. Both read `dbo.fv_anomalies_at_cursor`. |
| Proxy endpoint | `src/ClimaSense.Web/ML/MLProxyEndpoints.cs` | `POST /api/ml/run/anomalies` proxies the .NET tier through to the ml-tier `POST /api/anomalies/detect`. Default body of `{ types: [sensor_failure, residual_outlier, regime_shift] }` is honoured when the caller omits one. |
| Dashboard card | `src/ClimaSense.Web/Pages/Index.cshtml` | "Last anomaly" card between the latest-reading and comfort cards. Type pill colour-coded per `anomalyType` (sensor_failure red / regime_shift amber / residual_outlier blue). Hydrates via single `fetch('/api/anomalies/latest')` on page load; renders "none yet" pill on 404. |
| Contract | `contracts/openapi.yaml` | Promotes `/api/anomalies/detect` from 501-stub to real handler (responses now `200` / `503` only — 502/504 dropped since the ml tier IS the source). `AnomalyDetectResponse` extended with `perType: AnomalyRunSummary`. Adds `/api/anomalies/latest` + `/api/anomalies` (web-tier only) and `LatestAnomalyResponse` + `AnomaliesResponse` schemas. `info.version` bumped to `0.8.0-slice-8`. |
| Validator exclusions | `src/ClimaSense.ML/climasense_ml/contract_validator.py` | `/api/anomalies/latest` + `/api/anomalies` added to `_ML_TIER_EXCLUDED_PATHS`; `LatestAnomalyResponse` + `AnomaliesResponse` added to `_DROP_SCHEMAS`. |
| Generated schemas | `src/ClimaSense.ML/climasense_ml/schemas/generated.py` | Hand-edited to add `AnomalyRunSummary` Pydantic class + `per_type` field on `AnomalyDetectResponse`. The Dockerfile re-runs `datamodel-code-generator` at image-build time which produces an identical file from the updated YAML; the hand-edit is the bootstrapping path for local pytest runs. |
| Runtime dep | `src/ClimaSense.ML/pyproject.toml` + `Dockerfile` | Adds `ruptures>=1.1.9,<2` (PELT changepoint detector). arm64+linux64 wheels ship binaries; no C-extension build at install. |
| ADR | `docs/adr/0016-three-detector-anomaly-implementation.md` | Records hyperparameters (`pen=10`, z-threshold=3, gap=10min, stuck=5, range=[−10,50]/[0,100]), idempotency strategies, scheduler, module layout. |
| Golden test 3 | `src/ClimaSense.ML/tests/test_breach_gaps_and_islands_synthetic.py` | Synthetic `SensorReadings` with known gap + stuck-run + out-of-range readings; calls `SensorFailureRules.scan_recent`; asserts exactly three anomalies (one per rule). Plus an idempotency sub-test that re-runs the same window and asserts ZERO new inserts. |
| Golden test 4 | `src/ClimaSense.ML/tests/test_changepoint_scan_and_replace_idempotent.py` | Synthetic 90-day daily-mean series with a known 4°C shift at index 50. Asserts PELT detects it within ±5 indices, the scan-and-replace transaction acquires the named applock, stale rows are wiped, and TWO runs produce an identical rowset (deterministic). |
| Detector unit tests | `src/ClimaSense.ML/tests/test_anomaly_sensor_failure.py`, `test_anomaly_residual.py`, `test_anomaly_changepoint.py` | Pinned-string SQL shape + behavioural fake-engine drills. The residual test stubs the forecaster so it doesn't depend on sklearn's coefficients. |
| Orchestrator tests | `src/ClimaSense.ML/tests/test_anomaly_orchestrator.py` | Aggregation correctness, exception re-raise (`run_all_detectors`) vs swallow (`run_safely`), per-type field names match wire contract. |
| Persistence shape tests | `src/ClimaSense.ML/tests/test_anomaly_persistence.py` | Pinned-string SQL shape — recent reads go through the cursor TVF and project every column the dashboard expects. |
| Router tests | `src/ClimaSense.ML/tests/test_anomaly_router.py` | End-to-end FastAPI TestClient: camelCase envelope with `perType` shape, 503 on un-fitted forecaster, rows surfaced from the read helper. Uses factory injection so the detectors are fake. |
| .NET read tests | `tests/ClimaSense.Web.Tests/AnomalyReadServiceTests.cs` | Service exercises null-handling, range defaults (24h lookback), 90-day cap, start>end rejection, null cursor guard, null fetcher guard, pinned SQL strings for both latest + range queries. |
| .NET endpoint tests | `tests/ClimaSense.Web.Tests/AnomalyEndpointTests.cs` | 200 with camelCase wire shape, 404 with `no_anomaly_yet`, range with default window, type filter validation, ISO-8601 parse validation. |

## Surface added

```text
POST /api/anomalies/detect          # ml tier — three-detector pipeline (replaces slice-2 501)
GET  /api/anomalies/latest          # web tier — read latest Anomalies row at cursor
GET  /api/anomalies?start=&end=&type= # web tier — range query with optional type filter
POST /api/ml/run/anomalies          # web tier — proxy to ml's POST /api/anomalies/detect
```

## How to validate (live)

```bash
# Bring the stack up.
docker compose up -d --wait

# Watch the ml tier log; expect "Anomaly scheduler: started" + per-tick lines.
docker compose logs ml | grep -E "Anomaly scheduler|anomaly_router|anomaly_changepoint"

# Trigger the pipeline on demand (idempotent on the cursor).
curl -X POST http://127.0.0.1:8080/api/ml/run/anomalies \
  -H "Content-Type: application/json" \
  -d '{"types":["sensor_failure","residual_outlier","regime_shift"]}'

# Per-type aggregate.
docker compose exec db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -No -d ClimaSense \
  -Q "SELECT AnomalyType, COUNT(*) FROM dbo.Anomalies GROUP BY AnomalyType;"

# Read the latest via the web tier (404 first run, 200 after the first detection).
curl http://127.0.0.1:8080/api/anomalies/latest

# Range query with a type filter.
curl "http://127.0.0.1:8080/api/anomalies?type=sensor_failure"

# Open the dashboard — the "Last anomaly" card sits between the
# latest-reading and the comfort card; type pill is colour-coded.
open http://127.0.0.1:8080/
```

## Idempotency receipts

```bash
# Sensor failure + residual outlier — net inserts on rerun should be 0.
docker compose exec db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -No -d ClimaSense \
  -Q "SELECT AnomalyType, COUNT(*) AS n FROM dbo.Anomalies GROUP BY AnomalyType;"

curl -X POST http://127.0.0.1:8080/api/ml/run/anomalies | jq .perType
# {sensorFailure: N, residualOutlier: M, regimeShift: K}
curl -X POST http://127.0.0.1:8080/api/ml/run/anomalies | jq .perType
# {sensorFailure: 0, residualOutlier: 0, regimeShift: K}
#                                                    ^^^^^^
# regime_shift is post-replace count (stable, not net inserts).

docker compose exec db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -No -d ClimaSense \
  -Q "SELECT AnomalyType, COUNT(*) AS n FROM dbo.Anomalies GROUP BY AnomalyType;"
# Counts unchanged from above.
```

## Hyperparameters (pinned in ADR-0016)

* SensorFailureRules: gap > 10 min, stuck >= 5 readings, T ∉ [−10, 50] °C, RH ∉ [0, 100] %.
* ResidualOutlierDetector: z-threshold = 3.0, rolling σ over 48 hours.
* ChangepointDetector: PELT penalty = 10.0, 90-day scan window, min 14 daily points to fire.
