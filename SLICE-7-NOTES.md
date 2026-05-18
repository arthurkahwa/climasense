# Slice 7 implementation notes (comfort scoring + golden test 2)

> Auxiliary working-notes for slice 7 (issue #9). Captures the
> reviewer-facing map of what landed and how to validate it.

## What landed

| Concern | Path | Notes |
|---|---|---|
| Pure scorer | `src/ClimaSense.ML/climasense_ml/comfort.py` | `ComfortCalculator.score(t_c, rh_pct, bucket_time, hemisphere)` — pure ASHRAE 55 graphical zone evaluator. Two hardcoded polygons (summer + winter), ray-cast point-in-polygon, Euclidean distance-to-segment for outside points. Hemisphere mapping: Apr–Oct → summer (N) / winter (S); Nov–Mar mirrored. `hemisphere_from_env()` reads `COMFORT_HEMISPHERE` (default `N`). |
| Persistence | `src/ClimaSense.ML/climasense_ml/comfort_persistence.py` | `upsert_comfort_score` MERGEs into `dbo.ComfortScores` keyed on `BucketTime` (idempotent via the schema's UNIQUE constraint). `read_latest_comfort_at_cursor` + `read_recent_comfort_at_cursor` read through `dbo.fv_comfortscores_at_cursor(@asOf)`. |
| Emitter | `src/ClimaSense.ML/climasense_ml/comfort_emitter.py` | `score_at_cursor` + `emit_comfort` compose SQL trailing-hour mean + pure scorer + upsert. `ComfortEmitter` wraps `emit_comfort` with the β-prime gate (slice-5 pattern) — fires every wall-minute, emits one row per replay-hour. |
| Router | `src/ClimaSense.ML/climasense_ml/comfort_router.py` | Real `GET /api/comfort/score?hours=24` handler. Recomputes + MERGEs every call (idempotent on cursor's bucket). Returns 503 with `error: empty_window` when the trailing window has no readings. |
| Lifespan wiring | `src/ClimaSense.ML/climasense_ml/main.py` | Imports the comfort router/emitter; registers `build_comfort_router(...)` BEFORE the stub router; `_register_comfort_scheduler(app)` runs after the leaderboard seeder. `get_hemisphere` is a module-level singleton. App version bumped to `0.7.0-slice-7`. |
| Stub cleanup | `src/ClimaSense.ML/climasense_ml/stubs.py` | Comfort stub removed (handler is real now); `Query` + `ComfortScoreResponse` imports dropped. Module docstring updated. |
| Stub test | `src/ClimaSense.ML/tests/test_stubs_return_501.py` | `/api/comfort/score` and `/api/comfort/current` added to `_REAL_ENDPOINTS`; floor dropped from 3 to 2 (anomalies + profiles remain). `CLIMASENSE_SKIP_COMFORT_SCHEDULER=1` for test fixture parity. |
| Web read service | `src/ClimaSense.Web/Comfort/` | `ComfortReadService` + `SqlComfortFetcher` + `CurrentComfortDto`. Mirrors slice-5/6 delegate-seam pattern; no `IComfortReadService` interface (ADR-0011). |
| Web endpoint | `src/ClimaSense.Web/Program.cs` | `GET /api/comfort/current` registered alongside slice-3/4/5/6 reads. Reads `dbo.fv_comfortscores_at_cursor`; 404 with `error: no_comfort_yet` when no row visible. |
| Proxy endpoint | `src/ClimaSense.Web/ML/MLProxyEndpoints.cs` | `POST /api/ml/run/comfort` proxies to ml's `GET /api/comfort/score?hours=24` per spec. Returns the envelope verbatim; the side-effect (MERGE into ComfortScores) is the on-demand "Run Comfort" behaviour. |
| Dashboard card | `src/ClimaSense.Web/Pages/Index.cshtml` | Comfort card next to latest-reading + heartbeat. Shows score (0–100), rating, season. Hydrates via single `fetch('/api/comfort/current')` on page load. Colour-codes rating; gracefully renders em-dashes on 404 / error. |
| Contract | `contracts/openapi.yaml` | Promotes `/api/comfort/score` from 501-stub to real handler (responses now `200` / `503` only — 502/504 dropped since the ml tier IS the source). Adds `/api/comfort/current` (web-tier only) and `CurrentComfortResponse` schema. `info.version` bumped to `0.7.0-slice-7`. |
| Validator exclusions | `src/ClimaSense.ML/climasense_ml/contract_validator.py` | `/api/comfort/current` added to `_ML_TIER_EXCLUDED_PATHS`; `CurrentComfortResponse` added to `_DROP_SCHEMAS`. |
| ADR | `docs/adr/0015-comfort-scoring-implementation.md` | Records the pinned polygon vertices, `α = 4.0` scoring constant, hemisphere mapping, scoring formula, and module layout. Amends ADR-0005. |
| Golden test 2 | `src/ClimaSense.ML/tests/test_comfort_polygons_seasonal_boundary.py` | 12+ tests: summer + winter polygon centroids, 3 interior points each, Apr/May and Oct/Nov boundary in N, hemisphere mirror for 4 months, far-outside saturation, RH clamping, deterministic discipline, vertex regression for every polygon vertex. |
| Emitter tests | `src/ClimaSense.ML/tests/test_comfort_emitter.py` | β-prime tests: first tick emits, second tick within cadence is gated, third tick after cadence emits again, exception swallow, empty window → None, idempotency on rerun, hemisphere flip. Uses an in-memory `_FakeEngine` so no SQL Server needed. |
| Router tests | `src/ClimaSense.ML/tests/test_comfort_router.py` | End-to-end FastAPI TestClient: camelCase envelope, idempotency on rerun, hemisphere flip, 503 on empty window. Monkeypatches `_load_trailing_mean` to skip SQL. |
| Persistence shape tests | `src/ClimaSense.ML/tests/test_comfort_persistence.py` | Pinned-string SQL shape: MERGE targets `dbo.ComfortScores`, keys on `BucketTime`, updates Score/Rating/Season. Latest + recent reads go through the cursor TVF. |
| .NET read tests | `tests/ClimaSense.Web.Tests/ComfortReadServiceTests.cs` | Service exercises null-handling, value round-trip, cancellation, null cursor guard, null fetcher guard, pinned SQL string. |
| .NET endpoint tests | `tests/ClimaSense.Web.Tests/ComfortEndpointTests.cs` | 200 with camelCase wire shape, 404 with `no_comfort_yet` when fetcher returns null, idempotency of two consecutive reads. |

## Surface added

```text
GET  /api/comfort/score           # ml tier — pure score + MERGE upsert (replaces slice-2 501)
GET  /api/comfort/current         # web tier — read latest ComfortScores row at cursor
POST /api/ml/run/comfort          # web tier — proxy to ml's GET /api/comfort/score
```

## How to validate (live)

```bash
# Bring the stack up.
docker compose up -d --wait

# Watch the ml tier log; expect a "Comfort scheduler: started" line.
docker compose logs ml | grep -E "Comfort scheduler|ComfortEmitter"

# After the first scheduled tick fires:
docker compose exec db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -No -d ClimaSense \
  -Q "SELECT TOP 5 BucketTime, Score, Rating, Season FROM dbo.ComfortScores ORDER BY BucketTime DESC;"

# Read the latest via the web tier.
curl http://127.0.0.1:8080/api/comfort/current

# Trigger an on-demand recompute (idempotent on the cursor's bucket).
curl -X POST http://127.0.0.1:8080/api/ml/run/comfort

# Open the dashboard — the comfort card sits next to the latest-reading.
open http://127.0.0.1:8080/
```
