# ADR-0018 — `dbo.ReplayState` single-row table as the cross-tier cursor source

> Status: accepted (slice 13 / 2026-05-20, codifying slice 12).
> Refines ADR-0004 (replay mode + `IClock`) and ADR-0011
> (CursorSnapshot scope-singleton).

## Context

ADR-0004 introduced `ReplayClock` and the demo controls panel; ADR-0011
introduced the `CursorSnapshot` scope-singleton. Neither pinned down
**where the cursor state actually lives**. Three options were open
during slice 12 planning:

1. **.NET in-memory state, broadcast to FastAPI via SSE / HTTP.** The
   .NET tier owns the demo controls (`POST /api/clock`); FastAPI
   subscribes for cursor updates.
2. **FastAPI in-memory state, broadcast to .NET via SSE / HTTP.**
   Symmetric to (1) with the roles inverted.
3. **A SQL row both tiers read.** Mutation goes through the .NET tier
   (which owns the demo controls UI); both tiers read the row on every
   `CursorSnapshot` construction.

Options 1 and 2 require a second messaging plane (an SSE channel
between the two server tiers, separate from the browser-facing
`AlertStream`). Option 3 requires no new transport — both tiers
already speak to SQL Server.

## Decision

**Cursor state is a single SQL row.** Schema (in `scripts/init-db.sql`
§4.5):

```sql
CREATE TABLE dbo.ReplayState (
    Id                  TINYINT      NOT NULL CONSTRAINT PK_ReplayState PRIMARY KEY,
    IsPaused            BIT          NOT NULL,
    SpeedMultiplier     DECIMAL(7,2) NOT NULL,
    CursorAnchorWall    DATETIME2(7) NOT NULL,
    CursorAnchorReplay  DATETIME2(7) NOT NULL,
    UpdatedAt           DATETIME2(7) NOT NULL CONSTRAINT DF_ReplayState_UpdatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ReplayState_OneRow CHECK (Id = 1)
);
```

The `Id = 1` CHECK forbids more than one row. The PK is clustered on
`Id` so reads cost microseconds.

### Projection math

The cursor's current value is computed from the row at read time:

```
project(wall_now) =
    CursorAnchorReplay                                  if IsPaused
  | CursorAnchorReplay + (wall_now - CursorAnchorWall) * SpeedMultiplier   otherwise
```

`ReplayState.ProjectReplayTime(wallNow)` (mirrored in .NET and Python)
is a pure function — given the row + a wall-time, the replay-time is
deterministic.

### Mutations re-anchor

Each `POST /api/clock` action updates the row so the new state is
consistent with the wall-time at which the request was served:

- **Pause** — `IsPaused=true`; `CursorAnchorReplay = project(wall_now)`;
  `CursorAnchorWall = wall_now`. The cursor freezes at exactly the
  projected position at the moment pause was processed.
- **Resume** — `IsPaused=false`; re-anchor at `(wall_now,
  CursorAnchorReplay)`. The cursor continues from where it paused —
  no jump.
- **Seek** — `IsPaused` unchanged; re-anchor at `(wall_now, target)`.
- **SetSpeed** — capture `current = project(wall_now)`; set
  `SpeedMultiplier`; re-anchor at `(wall_now, current)`. The
  transition is smooth (no time jump).

### `ReplayClock` reads the row on every `UtcNow()`

The .NET adapter uses a single-row `SELECT` on the clustered PK. The
Python adapter is identical. `CursorSnapshot.FromClock(clock)` (the
scope-singleton from ADR-0011) bounds the read to **once per logical
operation** — typical request cost is one row read.

### `clock-changed` SSE event is a notification, not the source of truth

After every mutation, the .NET tier broadcasts a `clock-changed` event
on the slice-1 `AlertStream`. Browsers and the demo-controls UI
re-fetch `/api/clock` to refresh. **The Python tier does NOT subscribe
to this event** — it re-reads `dbo.ReplayState` on the next
`CursorSnapshot.FromClock` call (which lands within one wall-minute on
any of the three β-prime scheduled jobs).

### Seed values + idempotency

`init-db.sql` seeds the row via `MERGE WHEN NOT MATCHED` so re-running
the script leaves the existing state untouched:

```sql
MERGE dbo.ReplayState AS T
USING (VALUES (1, 0, 60.00, SYSUTCDATETIME(), '2016-01-20T00:00:00')) AS S(...)
ON T.Id = S.Id
WHEN NOT MATCHED THEN INSERT (...) VALUES (...);
```

Default speed 60× per ADR-0004. Initial cursor at `2016-01-20T00:00:00`
— roughly the start of the dataset.

## Consequences

- **No second messaging plane.** Both server tiers already speak to
  SQL Server; the cursor reads piggyback. The `clock-changed` SSE
  event is browser-only.
- **Read cost is bounded.** `CursorSnapshot` (ADR-0011) caps reads at
  one per logical operation. A single-row clustered-PK SELECT is
  microseconds.
- **In-flight job survival is structural.** A scheduled job that
  began before a seek captured the pre-seek cursor in its
  `CursorSnapshot`. The mid-job seek lands in the DB; the job
  finishes with the old cursor; the *next* job's snapshot picks up
  the new cursor. No cancellation tokens; no rollback. The DB stays
  monotonic append-only with respect to cursor moves.
- **Cross-tier parity is enforced by code review.** The .NET
  `ReplayState` record and the Python `ReplayState` dataclass are
  hand-mirrored — no generated bridge. The `ProjectReplayTime` math
  is the load-bearing surface; both tier's tests pin it
  (`ReplayStateTests.cs` + `test_replay_clock.py`).
- **`IReplayStateRepository` ships as an interface from day 1**
  because two adapters ship together (SQL adapter + test fake). Per
  ADR-0011 + ADR-0017, that qualifies — it does not violate the
  one-adapter-is-not-an-interface rule.
- **Wall mode degenerates the row.** When `CLIMASENSE_CLOCK_MODE=wall`,
  the `WallClock` adapter ignores `dbo.ReplayState` entirely.
  `GET /api/clock` returns `{mode: "wall", cursor: wall_now, ...}`;
  `POST /api/clock` returns 409 `clock_immutable_in_wall_mode`.
- **`UpdatedAt` is set by SQL Server** (`DEFAULT SYSUTCDATETIME()`),
  not by the .NET service. This gives a canonical monotonic timestamp
  without coordinating clocks across tiers.

## Alternatives considered

- **In-memory state on either tier with SSE replication.** Rejected
  because (a) requires a second messaging plane; (b) survives only
  within a single process — a `ml`-container restart would lose the
  cursor unless we persist it anyway; (c) introduces an order-of-events
  contract between two server tiers that would need its own test
  surface.
- **A file-based cursor (e.g. `/tmp/replay-state.json`).** Rejected
  because the two tiers run in separate containers — a shared volume
  works but adds compose surface area and a write-after-read race
  window that SQL Server resolves naturally with PK contention.

## References

- ADR-0004 — replay mode + `IClock` abstraction (the parent decision).
- ADR-0011 — `CursorSnapshot` scope-singleton + interface-emergence
  policy (justifies the per-request bounded read cost).
- ADR-0017 — test-affordance policy (justifies
  `IReplayStateRepository` having a fake adapter from day 1).
- Slice 12 PR / SLICE-12-NOTES.md — implementation evidence,
  cross-tier ProjectReplayTime parity tests, demo-controls UI.
