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
