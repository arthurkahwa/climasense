# ClimaSense — Domain Glossary

This file names the load-bearing concepts in the platform. Use these terms exactly across code, ADRs, and discussions; consistent language is the point.

The architectural vocabulary (module / interface / depth / seam / adapter / leverage / locality) is defined separately in [LANGUAGE.md](https://github.com/.../LANGUAGE.md) — this file is the **domain** layer.

---

## Cursor

The replay clock's current position in historical time. A `DateTime` in the data's time domain (typically a moment between 2016-01-20 and 2026-05-07 for the current dataset). The cursor is **mutable** — pause/resume/seek/setSpeed via `POST /api/clock` change it. Persisted in the `ClockState` table so restarts preserve position.

Distinct from wall-clock time; the cursor advances at the configured replay speed (default 60×) when not paused, against wall-clock advancement.

## CursorSnapshot

An **immutable snapshot** of the cursor at the entry of a logical operation (HTTP request, scheduled job tick, on-demand endpoint invocation). The structural enforcement of the rule "read `clock.now()` once per operation."

**Lifetime:** scoped to one logical operation. Constructed once on first resolve from DI (.NET) or via `contextvars` (Python). Never mutated. A long-running operation that needs a fresh cursor must construct a new snapshot explicitly — there is no `Refresh()`.

**Operations:**

| Operation | Returns | Purpose |
|---|---|---|
| `as_of` | `DateTime` | The frozen cursor value. |
| `clip(queryBuilder)` | builder with WHERE clause appended | Adds cursor-clipping (`<= @as_of`) to a SQL query against `SensorReadings` (raw table). |
| `windowed(duration)` | `(start, end)` tuple | Produces a scan window `(as_of - duration, as_of)`. Used by anomaly detectors and the alert engine. |
| `should_emit(last_emit, cadence)` | `bool` | The β-prime emission gate. Returns true iff `as_of - last_emit >= cadence`. Used by the three replay-cadence jobs. |

**Cursor-clipping for derived tables is NOT done via `clip()`.** It's done in the schema: `init-db.sql` defines `dbo.fv_<table>_at_cursor(@asOf)` inline table-valued functions for `Forecasts`, `Anomalies`, `DayProfiles`, `ComfortScores`, `Alerts`. Read queries select from the function, not the bare table. This makes cursor-clipping a property of the schema, not of caller discipline.

**Cross-language contract:** mirrored hand-written implementations in .NET (`ClimaSense.Web/Cursor/CursorSnapshot.cs`) and Python (`climasense_ml/cursor.py`). Parity enforced by code review against this entry.

## Replay mode / Wall mode

The two operating modes of the platform, selected via `CLIMASENSE_CLOCK_MODE`:

- **Replay mode** (default): cursor is sourced from `ReplayClock`. Historical data is replayed at configurable speed. Demo controls UI is registered. Per-minute incremental ingestion is unscheduled.
- **Wall mode**: cursor is sourced from `WallClock` and equals wall-clock time. Demo controls UI is hidden. Per-minute incremental ingestion is registered against an upstream MS SQL Server view.

Mode is fixed for the lifetime of a process; switching modes requires a restart.

## Boot-fit

The discipline by which `LagLinearForecaster` reconstructs its frozen coefficients at FastAPI startup. Re-fits sklearn `LinearRegression` against the notebook's held-out training split (history minus the last 14 days). Coefficients are deterministic given fixed inputs; the same boot-fit produces the same coefficients run-to-run. The forecaster's `predict()` uses these coefficients for the lifetime of the process; there is no online retraining.

Distinct from "online learning" or "scheduled retraining" — both of which are explicitly out of scope.

## β-prime emission gating

The scheduling pattern used by the three replay-cadence jobs (forecast emission, comfort scoring, anomaly detection). Each job:

1. Fires on a fixed wall-time interval (every wall-minute).
2. At each fire, constructs a `CursorSnapshot` from the current `IClock`.
3. Calls `snap.should_emit(last_emit, cadence)` to decide whether to emit.
4. Emits and persists if the gate returns true; otherwise skips.

The cadence is in *replay-time* (e.g. 1 hour), not wall-time. This decouples emission rate from replay speed — at 60× the job emits roughly once per wall-minute; at 1× it emits roughly once per wall-hour.

The alert engine does **not** use β-prime gating; it polls every wall-minute and always evaluates breaches against a 24-hour replay-time lookback.

## Closure-only delivery

The contract of the threshold alert engine: an `Alerts` row is written *only* when a breach interval has closed (i.e. the most recent reading no longer satisfies the rule's condition AND the run was long enough to qualify). Open / in-progress breaches produce zero rows.

Consequence: the dashboard's "Last anomaly" / "Last alert" widgets are *terminal-event indicators*, not real-time-tracking indicators.

## Provenance (on `Leaderboard`)

A column distinguishing rows whose origin is the notebook (`'notebook'` — copied from `assets/results.json`) from rows computed at boot by the live `LagLinearForecaster` (`'live'` — produced by `LeaderboardSeeder`). Both kinds use the same 14-day held-out test window; the column is the audit trail for the source.

## Single-zone

The platform's scope: one logical sensor stream. There are no `LocationId`, `SensorId`, or `ZoneId` columns anywhere in the schema. Multi-sensor selection (if upstream has multiple sensors) is the upstream owner's responsibility — they expose a single-sensor view, named per `UPSTREAM_VIEW_NAME`.

## Notebook as calibration upstream

Every numeric constant in the platform's schema and code traces back to a notebook cell. The notebook is the *source* for: lag-LR coefficients (re-derivable at boot via boot-fit), leaderboard rows (`assets/results.json`), `DayProfiles.Pattern` thresholds (notebook-derived percentiles), comfort discomfort threshold default. When constants need updating, the notebook is the place; `init-db.sql` carries provenance comments citing the relevant cell.

## Interface emergence policy

ClimaSense follows the rule **one adapter = hypothetical seam, two adapters = real seam**. New interfaces are not scaffolded for speculative future adapters; they are extracted from two or more concrete classes at the moment the second adapter is needed.

Concrete consequences in the current build:

- **No `IForecaster` interface.** `LagLinearForecaster` is a concrete class. `ResidualOutlierDetector` depends on it directly. When a second forecaster arrives (none planned), the interface is extracted from the two concrete classes — informed by both shapes, not speculated from one. Walks back the "scaffold for future" framing in ADR-0009; replaced by this policy.
- **No `IAnomalyStrategy` interface.** The three detectors (`SensorFailureRules`, `ResidualOutlierDetector`, `ChangepointDetector`) are concrete classes with naturally-shaped public methods that reflect their actual semantics — `scan_recent(snap)` for the two point-in-time detectors, `rescan_window(snap, days)` for the changepoint detector. The nightly job and the on-demand endpoint orchestrate them by explicit named calls; differing scan windows and idempotency strategies are visible at the call site, not hidden behind a uniform `detect(start, end)` signature.

This policy is independent of the `IClock` interface, which is genuinely justified — `WallClock` and `ReplayClock` are two real adapters at the same seam, both shipped from day 1.
