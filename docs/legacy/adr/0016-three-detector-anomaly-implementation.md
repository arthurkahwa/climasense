# 16. Three-detector anomaly pipeline — slice 8 implementation pinning

Date: 2026-05-18
Status: Accepted

## Context

ADR-0002 picked the three-detector pipeline (`SensorFailureRules`,
`ChangepointDetector`, `ResidualOutlierDetector`) over a single
Isolation-Forest detector. ADR-0011 then walked back the
speculative `IAnomalyStrategy` interface — the three detectors are
concrete classes with naturally-shaped public methods, not a
uniform `detect(start, end)` seam.

Slice 8 (#10) lands the implementation. This entry pins the
implementation-time decisions that ADR-0002 and ADR-0011 left
unstated.

## Decision

### Module layout

| Module | Class / Function | Responsibility |
|---|---|---|
| `anomaly_sensor_failure.py` | `SensorFailureRules.scan_recent(snap)` | Three SQL window-function INSERTs (gap / stuck / out-of-range), 24h scan window, `WHERE NOT EXISTS` idempotency. |
| `anomaly_residual.py` | `ResidualOutlierDetector.scan_recent(snap)` | Consumes the boot-fitted `LagLinearForecaster`; flags rows where `|residual| / rolling_σ > z_threshold`. 24h scan window. |
| `anomaly_changepoint.py` | `ChangepointDetector.rescan_window(snap, days=90)` | PELT via `ruptures.Pelt(model='rbf')` on daily means. Transactional scan-and-replace inside `sp_getapplock @Resource='changepoint_scan'`. |
| `anomaly_orchestrator.py` | `run_all_detectors(snap, …)` + `run_safely(snap, …)` | Calls each detector by name; aggregates per-type counts into `AnomalyRunSummary`. Distinct method names reflect distinct contracts at the call site. |
| `anomaly_persistence.py` | `read_recent_rows(engine, snap, since)` | Read-side helper that goes through `dbo.fv_anomalies_at_cursor(@asOf)`. Used by the router to surface the rows that landed. |
| `anomaly_router.py` | `build_router(get_engine, get_cursor, get_forecaster)` | Real `POST /api/anomalies/detect` handler. Returns `AnomalyDetectResponse` with `perType: AnomalyRunSummary`. |

### Hyperparameters pinned by this entry

| Constant | Value | Source |
|---|---|---|
| `SensorFailureRules.GAP_THRESHOLD_MINUTES` | 10 | ADR-0002 |
| `SensorFailureRules.STUCK_RUN_LENGTH` | 5 | judgment call — see slice 8 PR |
| `SensorFailureRules.TEMPERATURE_MIN / MAX` | [−10, 50] °C | ADR-0002 |
| `SensorFailureRules.HUMIDITY_MIN / MAX` | [0, 100] % | ADR-0002 |
| `ResidualOutlierDetector.DEFAULT_Z_THRESHOLD` | 3.0 | notebook EDA §6.5 (residual distribution is approximately normal; ±3σ ≈ 0.27 % FPR) |
| `ResidualOutlierDetector.ROLLING_WINDOW_HOURS` | 48 | judgment call — long enough to dampen diurnal cycles in the rolling σ |
| `ChangepointDetector.DEFAULT_DAYS` | 90 | ADR-0002 |
| `ChangepointDetector.DEFAULT_PELT_PENALTY` | 10.0 | empirical sweep on synthetic series with known 4°C shift at index 50; this value finds the shift reliably without false positives on flat segments. Documented as a judgment call. |
| `ChangepointDetector.MIN_DAILY_POINTS` | 14 | floor — PELT becomes unstable on shorter windows |

### Idempotency strategies — visible at the call site

The two point-in-time detectors use **`INSERT … WHERE NOT EXISTS`**
gated by `UQ_Anomalies_TypeTime`. The changepoint detector uses
**scan-and-replace inside a transaction** because PELT can
re-classify the same input differently if the daily-mean series
grows (the algorithm's penalty term scales with `len(signal)`). To
keep the rowset stable across reruns at the same cursor, the
detector wipes its window-scoped rows and re-INSERTs in one
transaction:

```python
with engine.begin() as conn:
    conn.execute(sp_getapplock @Resource='changepoint_scan')
    conn.execute(DELETE FROM Anomalies WHERE type='regime_shift' AND in_window)
    for cp in breakpoints:
        conn.execute(INSERT INTO Anomalies …)
```

The applock guarantees the nightly job and the on-demand button
never race; both serialise on the resource name.

### Scheduler

A single APScheduler `cron` job at 02:00 UTC fires
`run_safely(snap, …)` once per wall-day. The wrapper swallows
per-detector exceptions so a transient SQL error in (say) the
changepoint scan does NOT silence the next night's full run. The
job is also re-callable on demand via `POST /api/anomalies/detect`,
which routes through the same orchestrator inside the HTTP request
scope.

### Web-tier read surface

`AnomalyReadService` mirrors the slice-5/6/7 delegate-seam pattern:
concrete class, no `IAnomalyReadService` interface. Two delegate
seams (`LatestAnomalyFetcher` + `AnomalyRangeFetcher`) so tests can
inject lambdas. Both reads go through
`dbo.fv_anomalies_at_cursor(@asOf)` — cursor-clipping is a property
of the schema.

Two endpoints:

* `GET /api/anomalies/latest` — 404 with `error: no_anomaly_yet`
  when no row exists; 200 with the camelCase envelope otherwise.
* `GET /api/anomalies?start=&end=&type=` — 200 with rows ordered by
  `ReadingTime DESC`; window defaults to `[cursor − 24h, cursor]`
  with a 90-day server-side cap.

The dashboard's "Last anomaly" card hydrates via a single fetch of
`/api/anomalies/latest`; the type pill is colour-coded
(`sensor_failure` red / `regime_shift` amber / `residual_outlier`
blue).

## Consequences

* The `ruptures` library joins the ml-tier runtime dependencies
  (pinned `>=1.1.9,<2`). Pure-Python implementation with optional C
  extensions; the arm64+linux64 wheels ship binaries so the
  Dockerfile installs without a build step.
* The three detector modules are independent. A change to one
  detector's SQL does NOT touch the other two. The orchestrator is
  the only consumer of all three.
* The differing scan windows and idempotency strategies are
  documented at the *method names* (`scan_recent` for the two
  append-only point-in-time detectors; `rescan_window` for the
  scan-and-replace changepoint detector). A reviewer reading the
  orchestrator sees three different method names and three
  different behavioural shapes — no `IAnomalyStrategy.detect(...)`
  to hide them behind.
* `AnomalyRunSummary` ships on the wire (per-type breakdown is
  surfaced to the dashboard) and inside Python (`run_all_detectors`
  return type). Two parallel definitions: the Pydantic generated
  schema and the orchestrator's dataclass. Field names match by
  convention (`sensor_failure`, `residual_outlier`, `regime_shift`)
  with the Pydantic class carrying camelCase aliases for the wire.
* Golden tests 3 and 4 lock the behavioural contract:
    * Test 3 (`test_breach_gaps_and_islands_synthetic.py`) — synthetic
      SensorReadings with known gap + stuck-run + out-of-range
      readings; asserts exactly one row per rule + idempotent rerun.
    * Test 4 (`test_changepoint_scan_and_replace_idempotent.py`) —
      synthetic daily-mean series with a known 4°C shift at index 50;
      asserts PELT picks up the shift (±5 indices), and that re-running
      `rescan_window` twice yields an identical rowset.
