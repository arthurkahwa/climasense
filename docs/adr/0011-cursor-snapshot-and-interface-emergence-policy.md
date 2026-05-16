# 11. CursorSnapshot value type, scoped lifetime, and the interface-emergence policy

Date: 2026-05-13
Status: Accepted

## Context

ADR-0004 introduced `IClock` and made every "now" call route through it.
What ADR-0004 did not pin down is *how often* a logical operation reads
`now()` — it implicitly assumed a single read. The problem is that
"single read" is caller discipline, not structure: an HTTP request that
reads the clock at the controller, then again at a service, then again
at the repository will silently drift if the cursor moves between reads.

The 2026-05-13 architecture review surfaced two related decisions that
ADR-0004 left unstated:

1. The "read clock once per logical operation" rule is too important to
   leave to convention. It should be **structurally** enforced.
2. The platform's earlier sketch (PRD #2 + class diagram) speculatively
   introduced `IForecaster` and `IAnomalyStrategy` interfaces. Each had
   exactly *one* implementation planned. That violates the
   one-adapter-is-not-an-interface heuristic and pollutes the codebase
   with seams that have no second adapter to validate them.

## Decision

### `CursorSnapshot` is the structural enforcement of one clock read per operation

Introduce a `CursorSnapshot` value type, mirrored hand-written in both
the .NET tier (`src/ClimaSense.Web/Cursor/CursorSnapshot.cs`) and the
Python tier (`src/ClimaSense.ML/climasense_ml/cursor.py`). Operations:

| Operation | Returns | Purpose |
|---|---|---|
| `as_of` | `DateTime` | The frozen cursor value. |
| `clip(query)` | `(query', param)` | Append `WHERE ReadingTime <= @asOf` to a SQL query against the *raw* `SensorReadings` table. |
| `windowed(duration)` | `(start, end)` | Produce a scan window `(as_of - duration, as_of)`. |
| `should_emit(last, cadence)` | `bool` | β-prime emission gate. |

**Lifetime:**

- .NET — registered as a *scoped* DI service. ASP.NET Core's per-request
  scope produces one snapshot per HTTP request; controllers and services
  receive it via DI.
- Python — bound via `contextvars` at request / job entry by
  middleware (`CursorScopeMiddleware`); FastAPI's `Depends` returns the
  bound snapshot.

**Cursor-clipping for derived tables is NOT done via `clip()`.** Five
inline table-valued functions (`dbo.fv_<table>_at_cursor(@asOf)`) live
in `scripts/init-db.sql` for `Forecasts`, `Anomalies`, `DayProfiles`,
`ComfortScores`, `Alerts`. Read queries select from the function rather
than the bare table. Cursor-clipping for derived tables is therefore a
property of the schema, not of caller discipline.

### Interface-emergence policy

Adopt the rule **one adapter = hypothetical seam, two adapters = real
seam**. New interfaces are not scaffolded for speculative future
adapters; they are extracted from two or more concrete classes at the
moment the second adapter is needed.

Concrete consequences:

- **No `IForecaster` interface.** `LagLinearForecaster` is a concrete
  class. `ResidualOutlierDetector` depends on it directly. When a second
  forecaster arrives (none planned), the interface is extracted from
  the two concrete classes — informed by both shapes, not speculated
  from one. This walks back the "scaffold for future" framing in
  ADR-0009.
- **No `IAnomalyStrategy` interface.** The three detectors
  (`SensorFailureRules`, `ResidualOutlierDetector`, `ChangepointDetector`)
  are concrete classes with naturally-shaped public methods that reflect
  their actual semantics — `scan_recent(snap)` for the two point-in-time
  detectors, `rescan_window(snap, days)` for the changepoint detector.
- `IClock` remains because it has *two* real adapters (`WallClock` and
  `ReplayClock`) at the same seam, both shipped from day 1. The policy
  is therefore not a blanket "no interfaces"; it is "no interfaces
  without two adapters."

## Consequences

- The discipline cost paid by every controller / service in both tiers
  is one DI lookup or one `Depends(get_cursor)`. In return: cursor
  drift inside an operation becomes a structural impossibility, not a
  convention.
- `clip()` covers raw `SensorReadings` only; derived tables must use
  the inline TVFs. Read-side authors who forget the TVF and select from
  the bare table get wrong answers — the tradeoff is that the TVF is a
  *visible*, statically-checkable artefact in `init-db.sql` rather than
  caller-side discipline.
- Future code that wants a two-detector orchestration (e.g. running
  two anomaly strategies side-by-side) will *not* find an
  `IAnomalyStrategy` to bind to; it will pass concrete dependencies
  explicitly. When the second forecaster arrives, ADR-0011 is the
  trigger to extract `IForecaster` from the two concrete classes —
  not before.
- `CursorSnapshot` is hand-written in both tiers. There is no
  generated cross-tier bridge: parity is enforced by code review
  against this entry and against `CONTEXT.md` → "CursorSnapshot."
- Slice 1 ships `WallClock` only; `ReplayClock` lands in slice 12. The
  registration sites in both tiers (`Program.cs` and
  `climasense_ml.main`) carry `// TODO(slice-12)` comments at the
  exact location where the conditional binding will live.
