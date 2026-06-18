# 4. Replay mode with `IClock` abstraction

Date: 2026-05-08
Status: Accepted

## Context

The README promised a "Live Dashboard" with a current-reading card, hourly forecast scheduler, and a "Last anomaly from prior 24 hours" view. The only data source is `sensor_data.csv` (~10 years ending 2026-05-07). There is no live ingestion path. Without a deliberate decision, every "scheduled" verb (`run hourly`, `nightly sweep`, `current reading`) was undefined behaviour after `docker compose up`.

Three modes were considered: replay (virtual cursor through historical data), ingest (`POST /api/readings` plus optional MQTT), and static (declare the dashboard frozen).

## Decision

Adopt **Replay mode**. A virtual cursor advances through the historical CSV at a configurable speed (default 60×). All "now" calls in both the .NET and Python services route through an `IClock` abstraction:

```csharp
public interface IClock { DateTime Now(); }     // .NET
class IClock(Protocol):                          # Python
    def now(self) -> datetime: ...
```

Implementations: `WallClock` (production-default future) and `ReplayClock` (demo). APScheduler triggers, EF Core default-value generators, latest-reading queries, and SSE timestamps all consult the active clock. SQL queries use parameterised `@as_of_time` values rather than `GETUTCDATE()` / `SYSUTCDATETIME()`.

A demo controls panel in the UI exposes pause / resume / seek and the speed multiplier.

## Consequences

- Every layer that touches "now" pays a one-time discipline cost: clocks injected via DI, no calls to `DateTime.UtcNow` outside the `WallClock` impl.
- The full pipeline (forecasts, anomalies, alerts, profiles, comfort) demos end-to-end against real data with zero deployment-day fuss.
- Forecast hourly cadence in replay-time = once per minute wall-time at 60×. Lag-LR re-fits in <1s — fine for the demo.
- ADR-0007 (alerts) and ADR-0001 (forecasts) are downstream consumers of this clock.
- README must declare Replay mode explicitly and explain the demo controls.
- Genuine live ingestion remains possible later by switching `IClock` to `WallClock` and adding a `POST /api/readings` endpoint.

## Amendment — 2026-05-20 (post-slice-13)

Slices 11 and 12 pinned down the seek + scheduler semantics that the original "demo controls panel" line left implicit. The full set of clarifications:

1. **Read clock once per logical operation.** Enforced structurally by the `CursorSnapshot` scope-singleton (.NET DI scoped service / Python `contextvars`). A mid-operation `POST /api/clock` seek lands in the DB, but the snapshot already captured the pre-seek value. The next request constructs a fresh snapshot and sees the new cursor. See CONTEXT.md → "CursorSnapshot" and ADR-0011.
2. **In-flight jobs complete with their pre-seek clock value** — no cancellation tokens, no abort. The DB is monotonic append-only with respect to cursor moves; a seek backwards never deletes rows.
3. **Every clock mutation broadcasts a `clock-changed` SSE event** on the slice-1 `AlertStream`. Browser handlers debounce and re-fetch.
4. **The cursor is a SQL row, not in-memory state.** `dbo.ReplayState` (single-row, PK=1) is the cross-tier source of truth. Both tiers read it via `IClock` adapters that project `(IsPaused, SpeedMultiplier, CursorAnchorWall, CursorAnchorReplay)` to the current replay time. See ADR-0018.
5. **β-prime emission gating.** APScheduler fires the three replay-cadence jobs (forecast, comfort, anomaly) on a fixed wall-minute cadence; each job constructs a `CursorSnapshot` and calls `snap.should_emit(last_emit, cadence)` to decide whether to emit. Speed-multiplier changes never reschedule a job. See CONTEXT.md → "β-prime emission gating."
6. **Cursor-clipping for derived tables is a schema property**, not caller discipline. Five inline TVFs (`dbo.fv_<table>_at_cursor(@asOf)`) live in `scripts/init-db.sql` for `Forecasts`, `Anomalies`, `DayProfiles`, `ComfortScores`, `Alerts`. Read queries select from the function, not the bare table. See ADR-0011.
7. **The `IClock` interface is justified** by the two-adapter rule (`WallClock` + `ReplayClock`, both shipped from day 1). Other interfaces touched by the design (`IForecaster`, `IAnomalyStrategy`) were dropped — see ADR-0011 / ADR-0017.
