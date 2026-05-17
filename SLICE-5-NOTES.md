# Slice 5 implementation notes (live forecast)

> Auxiliary working-notes for slice 5 (issue #7). Captures the
> reviewer-facing map of what landed and how to validate it.

## What landed

| Concern | Path | Notes |
|---|---|---|
| Forecaster | `src/ClimaSense.ML/climasense_ml/forecaster.py` | Concrete `LagLinearForecaster` — lag set `(1,2,3,6,12,24,48,168)` + sin/cos hour/dow + month, sklearn `LinearRegression`. Boot-fit replicates notebook §8.3 exactly (MAE=0.214410, RMSE=0.293336 to ≤ 1e-6 on the 336-hour held-out window). |
| Persistence | `src/ClimaSense.ML/climasense_ml/forecast_persistence.py` | `persist_forecast` bulk-inserts via OUTPUT-on-IDENTITY. `read_latest_forecast_at_cursor` goes through `dbo.fv_forecasts_at_cursor(@asOf)` so cursor-clipping is enforced by the schema (per ADR-0011). |
| Emission engine | `src/ClimaSense.ML/climasense_ml/forecast_emitter.py` | `emit_forecast` (on-demand) + `ForecastEmitter.emit_if_due` (APScheduler β-prime gate, cadence = 1 h replay-time). Tail history loaded from `SensorReadings` with the same hourly resample + linear interpolation the boot-fit uses. |
| FastAPI router | `src/ClimaSense.ML/climasense_ml/forecast_router.py` | Replaces the slice-2 forecast stubs with real handlers: `GET /api/forecast` → latest persisted batch (via TVF); `POST /api/forecast` → emit + persist + return envelope. |
| Stubs trimmed | `src/ClimaSense.ML/climasense_ml/stubs.py` | Forecast GET/POST removed; anomalies / profiles / comfort remain stubbed for slices 9 / 10 / 8. |
| Boot-fit lifecycle | `src/ClimaSense.ML/climasense_ml/main.py` | `_ForecasterTracker` mirrors the slice-3 bootstrap tracker. `_await_bootstrap_then_fit` chains the lag-LR fit after CSV bootstrap completes. `_register_forecast_scheduler` wires APScheduler. `/api/health/ready` adds a `forecaster` check that flips to `ok` when the fit completes. |
| Web read-path | `src/ClimaSense.Web/Forecasts/` | `ForecastReadService` + `SqlForecastFetcher` + `ForecastEnvelopeDto`. `GET /api/forecasts/latest` reads through the TVF; the ml container is NOT invoked. |
| Web emit proxy | `src/ClimaSense.Web/ML/MLProxyEndpoints.cs` | Adds `POST /api/ml/run/forecast` alongside the slice-2 `POST /api/ml/forecast`. Both call `IMLServiceClient.PostForecastAsync` and surface the failure-mapping (503 / 502 / 504) when the ml container is unreachable. |
| Explorer overlay | `src/ClimaSense.Web/Pages/Explorer.cshtml` + `wwwroot/js/explorer.js` | "Show" toggle adds a forecast line + 95 % CI band to the time-series chart. "Emit Forecast (72h)" button POSTs to `/api/ml/run/forecast` then re-fetches `/api/forecasts/latest`. |
| Contract | `contracts/openapi.yaml` | Adds `/api/forecasts/latest` (web-tier read-path). `info.version` bumped to `0.5.0-slice-5`. Pydantic regenerated; Kiota regenerated. |
| Validator exclusions | `src/ClimaSense.ML/climasense_ml/contract_validator.py` | `/api/forecasts/latest` added to `_ML_TIER_EXCLUDED_PATHS`. Forecast schemas remain in the validator (now ml-emitted). |
| Stub-test exclusions | `src/ClimaSense.ML/tests/test_stubs_return_501.py` | `/api/forecast` GET/POST removed from the stub list (they're real now); `/api/forecasts/latest` added to `_REAL_ENDPOINTS`. |
| Golden test 1 | `src/ClimaSense.ML/tests/test_lag_lr_matches_notebook.py` | 5 cases: numeric match to `assets/results.json` (1e-6 abs), determinism (1e-12 abs), fit-budget (≤ 5 s), predict-shape, and `IForecaster` Protocol absence. Skipped when `sensor_data.csv` is not bind-mountable. |
| .NET tests | `tests/ClimaSense.Web.Tests/ForecastReadServiceTests.cs` + `LatestForecastEndpointTests.cs` | 9 new tests covering the cursor-clip TVF wiring, empty-envelope shape, camelCase wire shape, and DI plumbing. |
| Deps | `src/ClimaSense.ML/pyproject.toml` + `Dockerfile` | Adds `scikit-learn>=1.5,<2`, `numpy>=1.26,<3`, `apscheduler>=3.10,<4` to both layers. |

## Surface added

```text
POST /api/forecast                # ml tier — emit lag-LR forecast + persist
GET  /api/forecast?horizonHours=  # ml tier — read latest persisted batch
GET  /api/forecasts/latest        # web tier — direct SQL read via TVF (bypasses ml)
POST /api/ml/run/forecast         # web tier — proxy to POST /api/forecast
```

Schema additions: none — the slice-2 contract already declared
`ForecastEnvelope`, `ForecastPoint`, and `ForecastRequest`. Slice 5
flips the corresponding paths from 501-stubs to real handlers.

## Verification

```sh
# Unit tests
dotnet test --nologo -p:RegenerateKiotaClient=false
# → 111 passed (was 102 in slice 4)

cd src/ClimaSense.ML && python3 -m pytest tests/
# → 48 passed (was 45 in slice 4 — added 5 golden, removed 2 forecast-stub cases)

# Compose lifecycle
cp .env.example .env
docker compose down -v
docker compose up -d --wait
# ~50 s later: db / ml / web all healthy.

docker compose logs ml | grep "LagLinearForecaster: fit complete"
# → "LagLinearForecaster: fit complete (n_train=89735, n_test=336, features=13, MAE=0.2144, RMSE=0.2933, 264 ms)"

# AC #4: POST /api/ml/run/forecast returns 200 with 72 points
docker exec climasense-web curl -fsS -X POST -H 'Content-Type: application/json' \
    -d '{"horizonHours": 72}' http://localhost:8080/api/ml/run/forecast | jq '.points | length'
# → 72

# AC #7: every Forecasts row has ModelVersion
docker exec climasense-db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
    -P "$MSSQL_SA_PASSWORD" -No -d ClimaSense \
    -Q "SELECT COUNT(*), COUNT(DISTINCT ModelVersion), MIN(ModelVersion) FROM dbo.Forecasts"
# → 72, 1, lag-lr-v1

# AC #5: /api/forecasts/latest reads via the inline TVF
docker exec climasense-web curl -fsS http://localhost:8080/api/forecasts/latest | jq '.horizonHours, .modelVersion'
# → 72, "lag-lr-v1"

# AC #8: ml down → 503 with non-leaking error
docker compose stop ml
docker exec climasense-web curl -sS -i -X POST -H 'Content-Type: application/json' \
    -d '{"horizonHours": 72}' http://localhost:8080/api/ml/run/forecast | head -8
# → HTTP/1.1 503, error: ml_service_unavailable, message: "ml tier unreachable…"
docker compose start ml

# AC #9: zero IForecaster references in src/
grep -r "IForecaster" src/ --include='*.py' --include='*.cs' | grep -v Generated
# → (empty)
```

## What was deliberately NOT built (deferred to later slices)

* **Comfort scoring** — slice 8.
* **Anomaly detection pipeline** — slice 9.
* **Day-of-week × hour-of-day profiles** — slice 10.
* **Threshold alert engine** — slice 11.
* **Leaderboard `live` row population** — the boot-fit produces the
  numbers; the seeder that MERGEs them into `dbo.Leaderboard` ships
  with the leaderboard UI in slice 6 (sibling slice).
* **Forecast retention policy** — `Forecasts` grows unbounded as the
  scheduler emits rows; cleanup is a future ADR (see PRD #2 "Out of
  Scope" → "Historical-forecast retention policy").
* **Confidence intervals for humidity** — the contract only carries
  `confidenceLowerTemp` / `confidenceUpperTemp`. A future contract
  change could add humidity CIs; for now we expose only the point
  estimate.

## Judgment calls

1. **Recursive multi-step prediction.** The notebook only validates
   one-step-ahead lag-LR on the held-out window. `predict()` extends
   this to multi-step by feeding the model's own predictions as the
   first lag for the next step. The error compounds, but the slice-7
   AC asks for "next 72h" and the dashboard overlay needs a full
   horizon to be useful. The golden test still locks the one-step
   accuracy via `evaluate_on_holdout`.

2. **CI bands from residual σ × 1.96.** The lag-LR has no closed-form
   uncertainty quantification at multi-step horizons; we use the
   in-sample residual standard deviation as a proxy (matches what
   most production lag-LR deployments do). Documented in
   `LagLinearForecaster` docstring.

3. **Humidity forecast uses the same feature shape as temperature.**
   The notebook never built a humidity-specific model. Reusing the
   temperature features is the smallest credible humidity forecaster;
   producing a humidity number that's "broadly correct" is enough for
   the dashboard. A future slice can replace this with a dedicated
   model if needed.

4. **APScheduler `BackgroundScheduler` instead of `AsyncIOScheduler`.**
   The emission body is sync (sqlalchemy + sklearn), so the
   background-thread executor is the simpler fit. Wrapping in
   `AsyncIOScheduler` would mean `asyncio.to_thread` inside the job,
   no win.

5. **β-prime gate sourced from `read_max_generated_at` (no in-memory
   `last_emit`).** A process restart preserves the gate state because
   the DB is the source of truth. Slight cost: one `SELECT MAX(...)`
   per wall-minute. Worth it for restart resilience.

6. **`/api/forecasts/latest` lives on the web tier, not ml.** The
   contract declares it under `tag: forecast` but its only
   implementation is the .NET service hitting SQL directly. Same
   pattern as slices 3 + 4 (`/api/readings/latest`, `/range`,
   `/heatmap`). Lets the Explorer overlay survive an ml outage.

7. **No `IForecaster` Protocol or interface anywhere.** Per ADR-0011:
   the seam emerges from two adapters, not one. The golden test
   `test_no_iforecaster_protocol_in_codebase` locks this.

8. **Forecast scheduler is `start`-ed via `BackgroundScheduler` in
   the lifespan, not registered as a FastAPI dependency.** APScheduler's
   `AsyncIOScheduler` would integrate with uvicorn's loop, but
   `BackgroundScheduler` on a worker thread is simpler and the
   emission is short (< 1 s). The scheduler is shut down by the
   lifespan teardown.

## .NET 10 GA gotchas (NONE re-introduced)

* Image tags unchanged: `mcr.microsoft.com/dotnet/sdk:10.0` and
  `aspnet:10.0`.
* `<InvariantGlobalization>false</InvariantGlobalization>` retained.
* No `switch` cases that differ only by nullable annotation.
* `DateTime.TryParse` styles still use `AssumeUniversal | AdjustToUniversal`
  (not mixed with `RoundtripKind`).

## Slice-3 / 4 regression checks

* `docker compose up -d --wait` brings all 4 containers healthy in
  ~30-50 s on a warm bootstrap (slice 3 bcp skipped) or ~60-90 s on
  a cold one.
* `curl http://127.0.0.1:8080/api/health/{live,ready}` returns 200.
* `curl http://127.0.0.1:8080/api/readings/latest` returns the
  slice-3 latest row.
* `curl http://127.0.0.1:8080/api/readings/range?...&bucket=hour`
  returns the slice-4 range data.
* `curl http://127.0.0.1:8080/api/readings/heatmap?year=2024` returns
  366 cells for the leap year.
* `curl -N http://127.0.0.1:8080/api/alerts/stream` emits
  `event: server-tick` frames.
* `docker compose logs ml | grep "ContractValidator: OK"` shows the
  validator passing with `(8 paths, 18 schemas)` — slice 5 added one
  path (`/api/forecasts/latest`), excluded as web-tier-only.
