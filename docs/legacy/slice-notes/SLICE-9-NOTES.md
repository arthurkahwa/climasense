# Slice 9 implementation notes (calendar-conditioned DayProfiles)

> Auxiliary working-notes for slice 9 (issue #11). Captures the
> reviewer-facing map of what landed and how to validate it.

## What landed

| Concern | Path | Notes |
|---|---|---|
| Threshold derivation script | `scripts/derive_pattern_thresholds.py` | One-shot reproducible deriver. Replays the same lag-LR + cohort z-score pipeline as the production `ProfileComputer`, then computes `p90(MaxAbsZscore)`, `p25/p75(MeanResidual)` over the full per-day population. Re-runs produce bit-identical numbers (OLS is closed-form; no randomness anywhere). Output: plain-text summary + SQL snippet + JSON payload (via `--json`). |
| Empirical thresholds in init-db.sql | `scripts/init-db.sql §4` | `dbo.fn_classify_pattern` updated from the slice-1 standard-normal Φ⁻¹ placeholders (1.281552 / ±0.6745) to the empirical numbers derived above: `p90(MaxAbsZscore)=3.059456`, `p75(MeanResidual)=0.027845`, `p25(MeanResidual)=-0.024658`. Training window: 2016-01-20 → 2026-05-07 (full `sensor_data.csv` after the same hourly-resample pipeline the forecaster uses). 90,239 hourly rows / 3,754 daily profiles. Provenance comment block cites the deriver script + reproducible methodology. |
| Pure compute | `src/ClimaSense.ML/climasense_ml/profile_computer.py` | `ProfileComputer.compute(history, target_dates=…)` — re-fits lag-LR on the FULL series (production-fit code path), computes one-step in-sample residuals, groups by `(day_of_week, hour_of_day)` cohort to derive μ/σ, z-scores each residual, aggregates per calendar date into `DayProfileRow(date, day_of_week, mean_residual, max_abs_zscore)`. Deterministic. No I/O. Per ADR-0011: concrete class, no `IProfileComputer` interface. |
| Persistence | `src/ClimaSense.ML/climasense_ml/profile_persistence.py` | `merge_day_profiles(engine, rows)` — MERGE keyed on `Date` with `Pattern` computed SQL-side via `dbo.fn_classify_pattern(MeanResidual, MaxAbsZscore)` (the empirical thresholds live in SQL, not Python). `read_day_profiles_at_cursor(engine, snap, start_date, end_date)` — reads through `dbo.fv_dayprofiles_at_cursor(@asOf)` so cursor-clipping is a property of the schema. `read_max_profile_date` for nightly-tick observability. |
| Emitter / orchestrator | `src/ClimaSense.ML/climasense_ml/profile_emitter.py` | `recompute_range(engine, snap, *, start_date, end_date, history_loader=…)` — composes load + compute + MERGE + re-read. Validates the range (`start ≤ end`, span ≤ 366 days). Idempotent on `[start, end]` (compute is deterministic + MERGE is keyed on Date + Pattern classifier is SQL-side and deterministic). `ProfileEmitter.tick()` is the nightly scheduler body: recomputes the last `NIGHTLY_LOOKBACK_DAYS=7` cursor-days. |
| FastAPI router | `src/ClimaSense.ML/climasense_ml/profile_router.py` | `POST /api/profiles/analyze` real handler (replaces slice-2 501-stub). 200 with `ProfilesAnalyzeResponse` envelope (rowsReplaced + camelCase rows); 400 on `start > end` / span > 366; 503 on empty history. |
| Lifespan + scheduler wiring | `src/ClimaSense.ML/climasense_ml/main.py` | App version bumped to `0.9.0-slice-9`. Profile router registered BEFORE the stub router (the stub router is now empty — every contract path the ml tier owns has a real handler). `_register_profile_scheduler` adds an APScheduler `cron` job at 03:00 UTC (one hour after the slice-8 anomaly cron at 02:00 UTC). Wall-time cron under WallClock; under slice-12 ReplayClock the cursor at the tick moment drives the recompute window. Skip via `CLIMASENSE_SKIP_PROFILE_SCHEDULER=1`. |
| Stub router cleanup | `src/ClimaSense.ML/climasense_ml/stubs.py` | `/api/profiles/analyze` 501 stub removed. Module docstring updated; the router is retained empty so future slices can land 501s during build-out without restructuring imports. |
| Web read service | `src/ClimaSense.Web/Profiles/` | `ProfileReadService` + `SqlProfileFetcher` + `DayProfileDto`/`DayProfilesResponseDto`. Mirrors slice-5/6/7/8 delegate-seam pattern. One delegate seam (`DayProfileRangeFetcher`); no `IProfileReadService` interface (ADR-0011). |
| Web endpoint | `src/ClimaSense.Web/Program.cs` | `GET /api/profiles?start=&end=` registered alongside slice-3/4/5/6/7/8 reads. Defaults to "last 30 days at the cursor". 400 on unparseable date / start > end / span > 366. Reads `dbo.fv_dayprofiles_at_cursor` directly — bypasses the ml tier. |
| Proxy endpoint | `src/ClimaSense.Web/ML/MLProxyEndpoints.cs` | `POST /api/ml/run/profiles` proxies the .NET tier through to the ml-tier `POST /api/profiles/analyze`. Default body `{ startDate: today-6, endDate: today }` (matches the nightly scheduler's lookback). |
| Contract | `contracts/openapi.yaml` | Promotes `/api/profiles/analyze` from 501-stub to real handler (responses now `200` / `400` / `503` — 502/504 dropped since the ml tier IS the source). Adds `/api/profiles` (web-tier-only) read with `DayProfilesResponse` + `DayProfileRowWithComputedAt` schemas. `info.version` bumped to `0.9.0-slice-9`. |
| Validator exclusions | `src/ClimaSense.ML/climasense_ml/contract_validator.py` | `/api/profiles` added to `_ML_TIER_EXCLUDED_PATHS`; `DayProfileRowWithComputedAt` + `DayProfilesResponse` added to `_DROP_SCHEMAS`. |
| Generated schemas | `src/ClimaSense.ML/climasense_ml/schemas/generated.py` | Regenerated via `scripts/regen-contracts.sh python`. Includes the new schemas + `Pattern` re-export. |
| Generated Kiota client | `src/ClimaSense.Web/Generated/MLClient/` | Regenerated by the project's `BeforeBuild` Kiota target. Adds `ProfilesRequestBuilder` (GET + POST under `/api/profiles*`) and `DayProfilesResponse` / `DayProfileRowWithComputedAt` POCOs. |
| Compute tests | `src/ClimaSense.ML/tests/test_profile_computer.py` | 9 tests: empty history, missing column, one row per date, determinism across reruns, target_dates filter projection (values match the full run), day-of-week correctness, max-abs-z non-negative, constant-series collapse. |
| Persistence tests | `src/ClimaSense.ML/tests/test_profile_persistence.py` | 7 tests: pinned MERGE shape (includes `fn_classify_pattern`), pinned read SQL through cursor TVF, empty MERGE no-op, range read column unpacking, `read_max_profile_date` empty + date + datetime cases. |
| Emitter tests | `src/ClimaSense.ML/tests/test_profile_emitter.py` | 7 tests: range validation (start>end, oversized), empty history skips MERGE, end-to-end orchestration with monkey-patched persistence, three-run idempotency (count stable), scheduler-tick computes the correct lookback window. |
| Router tests | `src/ClimaSense.ML/tests/test_profile_router.py` | 4 tests: 400 on invalid range, 400 on oversized range, 200 camelCase envelope shape, 200 empty result. Uses FastAPI TestClient with a synthetic `get_cursor` + monkey-patched persistence. |
| Threshold reproducibility test | `src/ClimaSense.ML/tests/test_derive_pattern_thresholds.py` | 3 tests: deriver reruns identical, output is three named floats, init-db.sql carries the empirical numbers (and the slice-1 placeholders are gone). |
| .NET read tests | `tests/ClimaSense.Web.Tests/ProfileReadServiceTests.cs` | 7 tests: null-fetcher guard, null-cursor guard, default end is cursor date, default start is `end-30 days`, explicit start/end honoured, start>end rejected, oversize window rejected, pinned `SqlProfileFetcher.RangeSql` (TVF + ORDER BY + window predicates). |
| .NET endpoint tests | `tests/ClimaSense.Web.Tests/ProfileEndpointTests.cs` | 6 tests: empty default range returns 200, 200 camelCase rows, 400 unparseable start, 400 unparseable end, 400 start>end, 400 oversized window. |

## Surface added

```text
POST /api/profiles/analyze          # ml tier — recompute DayProfiles + MERGE (replaces slice-2 501)
GET  /api/profiles?start=&end=      # web tier — read DayProfiles range at cursor (read-path bypass)
POST /api/ml/run/profiles           # web tier — proxy to ml's POST /api/profiles/analyze
```

## Pattern threshold provenance (path a — empirical)

The slice 1 PR shipped `dbo.fn_classify_pattern` with provisional
Φ⁻¹ standard-normal defaults (1.281552 ≈ p90; ±0.6745 ≈ p25/p75) and
a `TODO(slice-9)` marker. The notebook itself does not compute
per-cohort percentiles — section 8 of `Climate_Time_Series_Analysis.ipynb`
documents that no obvious cluster structure exists, which is why the
project pivoted from K-Means to deterministic z-score-driven labels
(ADR-0003).

Slice 9 derives the percentiles empirically by replaying the same
calendar-cohort residual pipeline the production `ProfileComputer`
uses against the full `sensor_data.csv` after the standard hourly-
resample + linear-interpolation:

```bash
python3 scripts/derive_pattern_thresholds.py
```

Output (committed into `init-db.sql §4`):

```
p90(MaxAbsZscore) = 3.059456
p75(MeanResidual) = 0.027845
p25(MeanResidual) = -0.024658
```

Training window: 2016-01-20 → 2026-05-07. 90,239 hourly rows;
3,754 daily profiles. Re-runs of the script produce bit-identical
numbers (OLS coefficients are deterministic; resampling +
interpolation are deterministic; numpy percentile is deterministic).

To re-derive after the data accumulates more:

```bash
python3 scripts/derive_pattern_thresholds.py --json
# Paste the resulting CASE expression into init-db.sql §4.
```

`test_init_db_sql_carries_empirical_thresholds` guards against
accidental drift between the script's output and the SQL constants.

## How to validate (live)

```bash
# Bring the stack up.
docker compose up -d --wait

# Watch the ml tier log; expect "Profile scheduler: started" + per-tick lines.
docker compose logs ml | grep -E "Profile scheduler|profile_router|profile_emitter"

# Trigger the recompute on demand (idempotent on the [start, end] range).
curl -X POST http://127.0.0.1:8080/api/ml/run/profiles \
  -H "Content-Type: application/json" \
  -d '{"startDate":"2026-05-10","endDate":"2026-05-17"}'

# Per-Pattern distribution.
docker compose exec db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -No -d ClimaSense \
  -Q "SELECT Pattern, COUNT(*) FROM dbo.DayProfiles GROUP BY Pattern;"

# Read the range via the web tier.
curl "http://127.0.0.1:8080/api/profiles?start=2026-05-10&end=2026-05-17"

# Default range (last 30 cursor-days).
curl http://127.0.0.1:8080/api/profiles
```

## Idempotency receipt

```bash
# Re-run the same window twice — rowsReplaced is stable; per-row
# (Pattern, MeanResidual, MaxAbsZscore) is unchanged.
curl -X POST http://127.0.0.1:8080/api/ml/run/profiles \
  -H "Content-Type: application/json" \
  -d '{"startDate":"2026-05-10","endDate":"2026-05-17"}' | jq .rowsReplaced
# 8
curl -X POST http://127.0.0.1:8080/api/ml/run/profiles \
  -H "Content-Type: application/json" \
  -d '{"startDate":"2026-05-10","endDate":"2026-05-17"}' | jq .rowsReplaced
# 8 (same — MERGE keyed on Date; compute is deterministic)
```

## Hyperparameters (pinned)

* `ProfileComputer`: LAGS + features identical to slice 5's
  `LagLinearForecaster.build_features` (cohort μ/σ derived from
  one-step in-sample residuals, not recursive multi-step).
* `ProfileEmitter`: `NIGHTLY_LOOKBACK_DAYS=7`, `MAX_RANGE_DAYS=366`.
* `ProfileReadService` (.NET): `DefaultLookbackDays=30`,
  `MaxLookbackDays=366`.
* `Pattern` thresholds: SQL CASE constants 3.059456 / 0.027845 / -0.024658
  (init-db.sql §4 — empirical).
