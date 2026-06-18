# ADR-0017 — Test-affordance policy: `InternalsVisibleTo` vs `public const`, and the cost of exposing test seams

> Status: accepted (slice 13 / 2026-05-20).
> Refines ADR-0011 (interface emergence policy). Captures a decision
> made implicitly across slices 4 → 12 that was never written up.

## Context

The 14-day build accumulated three kinds of seam that exist *for tests*:

1. **Domain interfaces** with two genuine adapters (`IClock`,
   `IReplayStateRepository`, `IMLServiceClient`). Justified by the
   two-adapter rule from ADR-0011 — both adapters ship.
2. **`public const string` SQL strings** on otherwise self-contained
   service classes (`SqlComfortBudgetFetcher.HoursOutsideZoneSql`,
   `SqlAlertReader.HistorySql`, `SqlReplayStateRepository.LoadSql` /
   `SaveSql`). Tests use these to assert that the executed query is
   the exact text shipped to SQL Server.
3. **`[assembly: InternalsVisibleTo("ClimaSense.Web.Tests")]`** on the
   web tier (added in slice 11 so `ThresholdAlertScannerTests` could
   drain `AlertStream.Frame` records via the channel reader without
   spinning a real SSE HTTP client).

Across slices 4 / 6 / 11 / 12 the *same* question got answered
differently each time: "this method/field needs to be visible to a test;
which exposure mechanism do I use?" Slice 4 rejected
`InternalsVisibleTo` (calling it "test-only API surface bleeding into
production"); slice 11 used it; slices 10 / 11 / 12 reached for
`public const string` for SQL strings; slice 12 made several methods on
`ReplayClockService` public.

The policy needs to be written down before slice 14 or the post-build
maintenance period multiplies the inconsistency.

## Decision

Adopt a **four-rung exposure ladder.** Pick the lowest rung that the
test actually needs.

| Rung | Mechanism | When to use | When NOT to use |
|---|---|---|---|
| 1 | Public domain method | The test exercises external behaviour (HTTP response, committed SQL row, return value of a documented method). | The test wants to inspect internal state. |
| 2 | `public const string` for SQL strings + algorithmic constants | The test pins the exact SQL text or the exact threshold value shipped to a database / external system. | The string is not externally observable (e.g. log-only). |
| 3 | `InternalsVisibleTo` | The test needs to read an internal channel, broadcast queue, or in-memory state that has no external HTTP / SQL projection. | The test could observe the same property through a public surface (rung 1 or 2). |
| 4 | Domain interface + adapters | Two real adapters ship from day 1 (or "today" if a second adapter is being added). | Only one adapter exists or is planned. (Use ADR-0011: speculative seams are forbidden.) |

### Rung 1 — Public domain method

Default. Slices 4 / 5 / 6 / 7 / 8 / 9 / 10 ship most service classes
with public methods; tests call them and assert on returns or on
SQL row counts. Most production code lives at rung 1.

### Rung 2 — `public const string`

SQL strings are *wire-format-adjacent*: their exact text is shipped to
SQL Server and a test that pins the string locks the contract between
the .NET tier and the SQL schema in `init-db.sql`. Examples in this
build:

- `SqlComfortBudgetFetcher.HoursOutsideZoneSql` (slice 10)
- `SqlAlertReader.HistorySql` (slice 11)
- `SqlReplayStateRepository.LoadSql` / `SaveSql` (slice 12)

`public const string` documents that the test relationship is by design.
Hand-editing the SQL without updating the test will fail the build.

### Rung 3 — `InternalsVisibleTo`

Reserved for cases where the test needs to observe something that has
no natural external projection. The only current use is
`AlertStream.Subscribe()` returning a `ChannelReader<Frame>`:
`ThresholdAlertScannerTests` reads frames directly off the channel to
assert "one mutation broadcasts one frame" without spinning an HTTP
SSE client.

`InternalsVisibleTo` is the standard .NET solution for this seam. The
alternative (exposing `Subscribe` as `public`) would let production
callers bypass the SSE HTTP transport and bind to the channel directly
— a production surface no caller should use.

### Rung 4 — Domain interface

Only when two real adapters ship together. Per ADR-0011:

- `IClock` — `WallClock` + `ReplayClock`, both from slice 1.
- `IReplayStateRepository` — `SqlReplayStateRepository` (production)
  + `FakeReplayStateRepository` (tests), both from slice 12.
- `IMLServiceClient` — `MLServiceClient` + `FakeMLServiceClient`,
  both from slice 2.

`IForecaster` and `IAnomalyStrategy` were dropped because each had
exactly one adapter — see ADR-0011 + the amendment on ADR-0009.

## Consequences

- **Future contributors have a written rubric.** "Just use
  `InternalsVisibleTo`" is no longer the default fix for "how do I test
  this?"
- **The four existing `public const string` SQL strings stay.** They're
  load-bearing — the post-build maintenance period will rely on their
  golden-pinning to catch SQL drift.
- **The single `InternalsVisibleTo` declaration on the web tier
  remains.** Removing it would require either spinning real HTTP SSE
  clients in `ThresholdAlertScannerTests` (10× the test runtime) or
  promoting `AlertStream.Subscribe` to `public` (production surface
  bleed).
- **`ReplayClockService`'s public methods are correctly placed at rung
  1.** They are part of the documented mutation surface (`POST
  /api/clock` action handlers); production callers depend on them.
- **A future "extract `IAnomalyStrategy` from two detectors" PR has a
  clear trigger.** When a fourth detector is genuinely needed, the
  interface is extracted at the moment of arrival — not before.

## References

- ADR-0011 — CursorSnapshot + interface-emergence policy (the
  two-adapter rule this ADR refines).
- ADR-0009's amendment — walks back the original "scaffold for future"
  interface framing.
- Slice 4 PR — first instance of the question (resolved as "no
  `InternalsVisibleTo`").
- Slice 11 PR — first use of `InternalsVisibleTo`.
- Slices 10 / 11 / 12 — sequential adoption of `public const string`
  for SQL.
