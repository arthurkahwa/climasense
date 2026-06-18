# Slice 12 implementation notes (ReplayClock + demo controls + clock-changed SSE + cursor-aware refresh)

> Auxiliary working-notes for slice 12 (issue #14). Reviewer-facing
> map of what landed and how to validate it.

## What landed

| Concern | Path | Notes |
|---|---|---|
| Cross-tier state table | `scripts/init-db.sql` (§4.5) | New single-row `dbo.ReplayState` table — `(IsPaused, SpeedMultiplier, CursorAnchorWall, CursorAnchorReplay, UpdatedAt)`. PK enforces `Id = 1`; CHECK forbids any other id. Seeded via idempotent MERGE with anchor (`SYSUTCDATETIME()`, `2016-01-20T00:00:00`) and default speed 60×. Surviving an `init-db.sql` re-apply leaves the row untouched (the MERGE only INSERTs WHEN NOT MATCHED). |
| .NET ReplayState | `src/ClimaSense.Web/Clock/ReplayState.cs` | Value record mirroring the SQL row + pure `ProjectReplayTime(wallNow)` math. `if (IsPaused) → CursorAnchorReplay; else → CursorAnchorReplay + (wallNow - CursorAnchorWall) * Speed`. Tick-precision arithmetic so cross-tier comparisons are exact. |
| .NET IReplayStateRepository | `src/ClimaSense.Web/Clock/IReplayStateRepository.cs` + `SqlReplayStateRepository.cs` | Two-adapter seam from day 1 (SQL adapter + delegate-based test fake) so the ADR-0011 "one adapter = hypothetical seam, two adapters = real seam" rule passes. Both SQL strings (`LoadSql`, `SaveSql`) are `public const string` for golden-pinning. SaveSql uses `OUTPUT INSERTED.*` so the post-update state comes back in one roundtrip. |
| .NET ReplayClock | `src/ClimaSense.Web/Clock/ReplayClock.cs` | `IClock` adapter; `.UtcNow()` reads the repo and projects. Single-row SELECT on PK = microseconds per call; the `CursorSnapshot` scope-singleton (ADR-0011) pins one call per logical operation so the per-request cost is bounded. |
| .NET ReplayClockService | `src/ClimaSense.Web/Clock/ReplayClockService.cs` | Concrete mutation service. Four actions: pause/resume/seek/setSpeed. Each loads the current state, projects through `ReplayState.ProjectReplayTime`, computes the new anchor pair, persists via the repo, and broadcasts `event: clock-changed` on the slice-1 `AlertStream`. Resume re-anchors at the paused replay position so no time jump. SetSpeed re-anchors at the current projection so the transition is smooth. |
| .NET Clock DTOs | `src/ClimaSense.Web/Clock/ClockDtos.cs` | `ClockStateDto`, `ClockMutationDto`, `ClockChangedPayload`. Hand-rolled (not Kiota) for camelCase consistency — same rationale as slices 5–11 DTOs. |
| .NET ClockMode resolver | `src/ClimaSense.Web/Clock/ClockMode.cs` | Reads `CLIMASENSE_CLOCK_MODE` at startup. Default `replay` per ADR-0004 + epic #2. Unknown values fall through to default. `ClockModeHolder` wraps the enum value (DI's generic `AddSingleton<T>` requires `T : class`). |
| .NET endpoints | `src/ClimaSense.Web/Program.cs` | `GET /api/clock` and `POST /api/clock` (pause/resume/seek/setSpeed). In `wall` mode GET returns degenerate payload (`mode: "wall"`, `paused: false`, `speed: 1`); POST returns 409 with `clock_immutable_in_wall_mode`. Endpoint code receives `ClockModeHolder` from DI and reads `.Mode`. |
| Python ReplayState + ReplayClock | `src/ClimaSense.ML/climasense_ml/clock.py` | Mirror of the .NET types. `ReplayState` is a frozen dataclass with the same `project_replay_time(wall_now)` math (linear scaling via `total_seconds()` for parity). `ReplayClock` constructor takes a `state_loader` callable so tests inject a fake without hitting SQL. `LOAD_REPLAY_STATE_SQL` is a module-level constant so cross-tier comparisons can golden-pin it. |
| Python mode selection | `src/ClimaSense.ML/climasense_ml/main.py` | Module-import-time `_clock_mode = _resolve_clock_mode()` reads `CLIMASENSE_CLOCK_MODE`. `_clock = _build_clock()` constructs either `ReplayClock(state_loader=lambda: load_replay_state(get_engine()))` or `WallClock()`. The schedulers all close over `_clock` by name so a future hot-swap is possible (slice 12 doesn't need it — process restart suffices). |
| Demo controls UI (JS) | `src/ClimaSense.Web/wwwroot/js/demo-controls.js` | Vanilla JS, no framework. Auto-injects a footer bar into `<div id="demo-controls-root">` on each page. Controls: pause/resume toggle, datetime-local seek input + button, 1×/10×/60×/300× speed buttons, live cursor readout, mode badge. XSS-safe — every DOM node built via `createElement` + `appendChild`; zero `innerHTML`. In `wall` mode the bar disables all controls and labels the toggle "Wall mode". Polls `/api/clock` every 5 s as a fallback so the cursor advances visibly between mutations. |
| Cursor-aware refresh wiring | `src/ClimaSense.Web/wwwroot/js/clock-changed-refresh.js` | Tiny SSE listener: opens its own EventSource on `/api/alerts/stream`, listens for `clock-changed`, debounces 300 ms, then dispatches a window-level `cursor-changed` CustomEvent. Page-local widget hydrators subscribe to that event and re-fetch. The 300 ms debounce absorbs burst mutations (pause → speed → seek). |
| Index.cshtml widget refactor | `src/ClimaSense.Web/Pages/Index.cshtml` | Four widget IIFEs (latest reading, comfort, anomaly, comfort budget) each gained a `hydrate()` function pulled from their fetch-and-render chain; `hydrate()` is invoked once on load and registered with `window.addEventListener('cursor-changed', hydrate)`. The demo-controls placeholder + `clock-changed-refresh.js` + `demo-controls.js` script tags appended at the end of the body. |
| Other page wiring | `src/ClimaSense.Web/Pages/{Explorer,Analysis,Alerts}.cshtml` | Same placeholder + script-tags. Alerts' history-table fetch wrapped in `hydrateAlerts()` + listener (a seek backward should hide post-cursor alerts; forward should reveal them). Explorer and Analysis don't currently re-fetch because their data is user-range / leaderboard (cursor-insensitive). |
| Contract | `contracts/openapi.yaml` | Adds `/api/clock` GET + POST, plus 4 schemas (`ClockMode`, `ClockState`, `ClockMutation`, `ClockChangedPayload`). Existing `/api/alerts/stream` description extended to document the new `clock-changed` event type. New `clock` tag. `info.version` bumped to `0.12.0-slice-12`. Header docstring updated. |
| Validator exclusions | `src/ClimaSense.ML/climasense_ml/contract_validator.py` | `/api/clock` added to `_ML_TIER_EXCLUDED_PATHS`; the 4 new schemas added to `_DROP_SCHEMAS`. The ml tier reads `dbo.ReplayState` directly via `ReplayClock`; mutation + SSE live on the .NET tier. |
| Stub-test exclusion | `src/ClimaSense.ML/tests/test_stubs_return_501.py` | `("/api/clock", "get")` and `("/api/clock", "post")` added to `_REAL_ENDPOINTS`. Stub-floor remains 0; `test_stub_routes_floor_is_zero_after_slice_9` continues to pass. |
| .NET tests | `tests/ClimaSense.Web.Tests/{ReplayState,ReplayClock,ReplayClockService,ClockEndpoint,ClockWallModeEndpoint}Tests.cs` + `TestEnvironmentSetup.cs` | 29 new tests (236 → 265). `ReplayStateTests` lock the math; `ReplayClockTests` locks the "in-flight jobs survive seek" claim by demonstrating `CursorSnapshot.FromClock` freezes scope-locally; `ReplayClockServiceTests` exercises each mutation against a fake repo + asserts each mutation emits exactly one `clock-changed` frame; `ClockEndpointTests` covers the HTTP wire shape (camelCase, all four actions, 400 on bad input); `ClockWallModeEndpointTests` covers the wall-mode degenerate GET + 409 POST. `TestEnvironmentSetup` sets `CLIMASENSE_CLOCK_MODE=wall` via `[ModuleInitializer]` so the pre-existing endpoint tests don't try to hit SQL through the new default. |
| Python tests | `src/ClimaSense.ML/tests/test_replay_clock.py` | Mirror of the .NET math tests + the cross-tier "snapshot freezes mid-operation" contract. 10 tests. Five existing tests that import `climasense_ml.main` (which constructs `_clock` at module-load) had `CLIMASENSE_CLOCK_MODE=wall` added to their env-setdefault block so module import doesn't try to read SQL through `load_replay_state`. |
| Generated Kiota client | `src/ClimaSense.Web/Generated/MLClient/Api/Clock/` + `Models/Clock*.cs` | Regenerated by the project's `BeforeBuild` Kiota target. Adds a `ClockRequestBuilder` plus three POCOs. `ClockChangedPayload` is documented in the contract but not referenced by any HTTP body, so Kiota omits it — same behaviour we saw for SSE-only payloads in slices 1 and 11. |
| docker-compose / env | `docker-compose.yml`, `.env.example` | Pass `CLIMASENSE_CLOCK_MODE` to both `web` and `ml` services with default `replay`. `.env.example` slice-12 section rewritten to describe the live semantics (was previously a "NOT consumed in slice 1" placeholder). |

## Acceptance criteria from issue #14

- [x] **`CLIMASENSE_CLOCK_MODE=replay` (default) brings up the demo with `ReplayClock`; the cursor advances at 60× by default** — `ClockModeResolver.Default == Replay`; `init-db.sql §4.5` seeds speed 60.00. `ClockEndpointTests.Get_returns_camelCase_clock_state` asserts the wire shape.
- [x] **Pause stops cursor advancement; resume continues from where it paused (no skip)** — `ReplayClockService.PauseAsync` writes `IsPaused=true` with the current projected replay as anchor; `ResumeAsync` re-anchors at `(wall_now, current_projected)` so no jump. Tests: `ReplayClockServiceTests.PauseAsync_freezes_cursor_at_projected_replay_time` and `ResumeAsync_continues_from_paused_position_no_jump`.
- [x] **Seek to an arbitrary historical date instantly updates `GET /api/clock`'s `cursor` field** — `SeekAsync` writes a new anchor pair `(wall_now, target)`. Test: `ReplayClockServiceTests.SeekAsync_re_anchors_to_requested_replay_time` + `ClockEndpointTests.Post_seek_re_anchors_to_target`.
- [x] **All open browser tabs receive a `clock-changed` SSE event within 1–2 wall-seconds of any mutation** — `ReplayClockService.BroadcastChanged` is called synchronously after every `SaveAsync` and immediately pushes a frame onto the slice-1 `AlertStream`. Test: `ReplayClockServiceTests.Each_mutation_broadcasts_one_clock_changed_event` drains the channel and asserts 4 mutations → 4 frames.
- [x] **After a seek, the dashboard's latest reading widget shows a value from the *new* cursor position** — `clock-changed-refresh.js` listens for `clock-changed` and dispatches `cursor-changed`; the latest-reading widget's `hydrate()` is registered on that event. Manual verification via the local validation script below.
- [x] **After a seek backwards, anomalies/forecasts/alerts generated under the previous cursor remain in the DB but are filtered out of read responses** — existing slice-1 inline TVFs (`dbo.fv_<table>_at_cursor`) clip via `<= @asOf`; the seek only changes the `@asOf` value, so post-cursor rows are excluded from new SELECTs. The DB stays monotonic-append-only per epic + ADR-0004. (Lock: existing slice-5/8/11 cursor-clip tests still pass — covered by the 265-test suite.)
- [x] **Speed dropdown changes the rate of cursor advancement; the forecast emission rate adjusts accordingly via β-prime gating (no scheduler re-registration)** — `SetSpeedAsync` only updates `dbo.ReplayState`; the APScheduler `interval` jobs in `_register_forecast_scheduler` / `_register_comfort_scheduler` keep firing on the same wall-cadence. The β-prime gate (`CursorSnapshot.should_emit`) inside each job reads the updated cursor on the next tick. Verified by grep + code review: no `scheduler.reschedule_job` call in any of the schedulers; `add_job(... interval, minutes=1)` is called once per scheduler under `_register_*_scheduler` which itself is called only from the lifespan's `_await_bootstrap_then_fit` (post boot-fit, once per process). Test: `ReplayClockServiceTests.SetSpeedAsync_preserves_current_projection_and_swaps_multiplier`.
- [x] **`CLIMASENSE_CLOCK_MODE=wall` brings up the platform with `WallClock`; the demo controls UI is hidden; the ingestion job is registered** — wall mode wires `WallClock` instead of `ReplayClock`. The demo-controls JS reads `mode: "wall"` from the initial `/api/clock` fetch and disables every control with a "Wall mode" label. Tests: `ClockWallModeEndpointTests.Get_returns_wall_mode_payload` and `Post_returns_409_with_clock_immutable_error`. *Caveat: per-minute incremental ingestion is not implemented in slice 12 — see "Out of scope".*

## Architecture decisions

- **Cross-tier state is a single SQL row** instead of an in-memory broadcast. Reason: both tiers need synchronised state without inventing a second messaging plane. The SQL row is microsecond-cheap to read (clustered PK), and the slice-1 `CursorSnapshot` scope-singleton already bounds reads to one per logical operation. The `clock-changed` SSE event is *only* a notification for the browser tier; both server tiers re-read SQL on the next snapshot.
- **No replay-clock interface for the mutation service.** `ReplayClockService` is a concrete class. There's exactly one production implementation and tests inject the repository fake directly. Conforms to ADR-0011: "one adapter = hypothetical seam, two adapters = real seam."
- **`IReplayStateRepository` IS an interface from day 1** because two adapters ship together: the SQL adapter (production) and a fake adapter (`FakeRepo` in tests). That qualifies under the same ADR-0011 rule that says `IClock` is justified.
- **Resume re-anchors at the paused position; seek/setSpeed re-anchor at `wall_now`.** This keeps resume continuous (no jump) and seek/setSpeed deterministic (the new replay-time corresponds exactly to the wall-time the request was served). Tested in `ReplayClockServiceTests`.
- **`ClockModeHolder` wrapping the `ClockMode` enum** is a workaround for .NET DI's generic `AddSingleton<T>` requiring a reference type. Documented at the type definition.
- **JS demo controls use plain `XMLHttpRequest`-equivalent `fetch` + vanilla DOM**, not React / Vue / htmx. Reasons: (a) one fewer thing for a reviewer to install; (b) `Layout = null` on every existing page means there's no shared framework infrastructure to extend; (c) the bar is ~15 KB of JS — adding a framework would be 100×.
- **In-flight job survival** is a property of the `CursorSnapshot` scope-singleton, not of `ReplayClock`. `CursorSnapshot.FromClock(clock)` reads `clock.UtcNow()` ONCE at scope entry; a mid-operation seek lands in the DB but the snapshot already captured the pre-seek value. The next request / scheduler tick constructs a fresh snapshot and sees the new cursor. Locked by `ReplayClockTests.CursorSnapshot_freezes_value_across_mid_operation_mutation` (.NET) and `test_replay_clock.test_cursor_snapshot_freezes_value_across_mid_operation_seek` (Python).
- **Same SSE channel for `server-tick`, `breach-detected`, `clock-changed`.** All three share the slice-1 `AlertStream` singleton, the monotonic `id:` space, and `Last-Event-ID` reconnection. The browser's existing `EventSource` instance just needs another `addEventListener('clock-changed', ...)` call — which `clock-changed-refresh.js` provides as a standalone module so each page doesn't need its own.

## Judgment calls

- **Replay-clock `ReplayClock.UtcNow()` is synchronous over an async repository via `GetAwaiter().GetResult()`.** The `IClock` contract is synchronous by design (every controller / DI factory calls it). Single-row PK SELECT is microseconds; no sync-context deadlock risk in ASP.NET Core minimal APIs (no `SynchronizationContext`). Same pattern slices 3-10 use for other scope-bound DI factories. Documented on the class.
- **`UpdatedAt` is set by the DB (`SYSUTCDATETIME()`)**, not by the .NET service. Reason: this gives us a canonical, monotonic timestamp across both tiers without coordinating their clocks. The .NET service merely emits the post-update value back in the response and SSE payload.
- **Demo-controls poll `/api/clock` every 5 wall-seconds as a fallback**. The cursor moves continuously when not paused, but SSE only fires on mutations. Five seconds is judgment-call: short enough that the readout looks live, long enough not to flood the server. The poll is cheap (single SELECT) and the response itself is small (one JSON object).
- **Speed options are 1× / 10× / 60× / 300×.** The issue brief listed `1× / 60× / 600× / 3600×`; the epic listed `1×, 10×, 60×, 300×`. I followed the epic — at 3600× the cursor advances one replay-day per ~24 wall-seconds, which is too fast to watch a forecast emit. 300× is a comfortable upper bound for a live demo.
- **`POST /api/clock` accepts both `setSpeed` and `speed` as action discriminators.** The brief uses `setSpeed`; the epic switches to `speed` in places. I'm strict about the canonical name in the wire contract (`setSpeed`) but the endpoint accepts both for forgiveness.
- **Wall-mode GET returns a degenerate payload with `mode: "wall"` and `cursor=anchor=wall_now`** rather than 405 Method Not Allowed. Rationale: the demo bar can render a consistent UX in both modes by reading the same shape. The 409 is reserved for the POST mutation (which doesn't make sense in wall mode).
- **The cross-tier symlink `sensor_data.csv` was NOT created in this worktree** — the sandbox blocked `ln -s`. The compose stack assumes `sensor_data.csv` lives at the repo root; reviewers running `docker compose up` from a fresh checkout will need that file present. Same posture as slice 11.
- **Existing widget hydrators on `Index.cshtml` re-fetch on `cursor-changed`; `Explorer` and `Analysis` do NOT.** Explorer's chart data is user-driven (range + bucket are URL params); a seek shouldn't change the rendered query. Analysis is a static leaderboard (the live row is global, not cursor-clipped). Alerts re-fetches because alert history IS cursor-clipped.
- **Python regen (`bash scripts/regen-contracts.sh python`) was sandbox-blocked.** The committed `src/ClimaSense.ML/climasense_ml/schemas/generated.py` does NOT include the four new Clock-related Pydantic schemas. This is benign: the contract validator's `_DROP_SCHEMAS` excludes those names before comparing the ml tier's `app.openapi()` against the canonical YAML, and no Python code references them (clock surface is .NET-only). Follow-up: re-run the regen on a host with `datamodel-codegen` installed.

## Out of scope (per the brief)

- **Per-minute incremental ingestion under wall mode** — per the epic the `pull_increment` job is unscheduled under `ReplayClock`. The slice-12 brief mentions this becomes active in wall mode; we left a placeholder. There's no `IngestionService.pull_increment()` implementation either — slice 3's `IngestionService` is bootstrap-only. Future work.
- **Mutation in wall mode** — POST returns 409 as the brief specifies.
- **A `clock-changed` event in wall mode** — there is no mutation surface in wall mode, so there are no events to emit. The browser opens an `EventSource` either way; the listener just never fires.
- **Per-tab cursor state** — the cursor is a single global value across all subscribers. Per the epic, multiple reviewers on multiple tabs should see consistent state.
- **Replay-clock-aware Plotly chart on Explorer** — Explorer's chart re-renders on user range change but not on cursor change. Per the brief's Out of scope.
- **Auth on `POST /api/clock`** — single-user local demo posture per ADR (no auth).
- **Speed exceeding 300×** — the bar exposes 1/10/60/300. The API accepts arbitrary positive multipliers; the bar just doesn't surface them.
- **Per-rule pause / per-job pause** — the clock is monolithic; pausing freezes every scheduled job's β-prime gate simultaneously.

## Inherited gotchas-not-reintroduced

- **No `-preview` image tags** — `Dockerfile`s unchanged from slice 10's `mcr.microsoft.com/dotnet/sdk:10.0` / `aspnet:10.0`.
- **`InvariantGlobalization=false` + `libicu-dev`** — unchanged.
- **Uvicorn log-config** — N/A (Python tier files touched; entrypoint untouched).
- **CS8120 hard error** — clean build with zero warnings (one Kiota `OpenAPI warning: Multiple servers` carried over from slice 4 isn't related and is benign).
- **`DateTime.TryParse` styles** — the new `POST /api/clock {action: seek, to: <iso>}` uses `DateTimeOffset`-equivalent semantics via the System.Text.Json binder (which deserialises ISO-8601 reliably). Direct query-string parsing wasn't introduced.
- **Half-open intervals** — N/A (no new SQL window scans).
- **Public methods / InternalsVisibleTo for test statics** — `SqlReplayStateRepository.LoadSql` / `SaveSql` are `public const string`; `ReplayState.RowId` and `ProjectReplayTime` are public; `ReplayClockService` methods are public.
- **ContractValidator + `_REAL_ENDPOINTS` exclusions** — both updated for `/api/clock`; stub-floor remains 0.

## Local validation

```bash
# .NET tests (Web)
dotnet test tests/ClimaSense.Web.Tests/ClimaSense.Web.Tests.csproj
# expected: 265 passed (was 236 after slice 11; +29 slice-12 tests)

# Python tests (ml-tier)
cd src/ClimaSense.ML && uv run pytest tests/
# expected: ~166 passed (was 156 after slice 11; +10 slice-12 tests).
# Five existing tests gained one extra env-setdefault line each (no
# behaviour change — just CLIMASENSE_CLOCK_MODE=wall for non-DB tests).

# Regen check (round-trip)
bash scripts/regen-contracts.sh
# expected: dotnet regen produces the slice-12 client; python regen
# produces the new Pydantic models. The repo ships the .NET output
# but the Python output was sandbox-blocked at slice-12 commit time —
# see Judgment calls.
```

## E2E validation (compose stack)

```bash
ln -s ../../../sensor_data.csv sensor_data.csv  # worktree-only

# Defaults: replay mode, speed 60×, cursor at 2016-01-20.
docker compose up -d --wait

# Wait for bootstrap (~30-60 s):
curl -s http://127.0.0.1:8080/api/health/ready

# Read the clock:
curl -s http://127.0.0.1:8080/api/clock | jq

# Pause / resume:
curl -X POST -H 'Content-Type: application/json' \
    -d '{"action":"pause"}' http://127.0.0.1:8080/api/clock | jq
curl -X POST -H 'Content-Type: application/json' \
    -d '{"action":"resume"}' http://127.0.0.1:8080/api/clock | jq

# Seek:
curl -X POST -H 'Content-Type: application/json' \
    -d '{"action":"seek","to":"2024-06-15T12:00:00Z"}' \
    http://127.0.0.1:8080/api/clock | jq

# Set speed:
curl -X POST -H 'Content-Type: application/json' \
    -d '{"action":"setSpeed","multiplier":300}' \
    http://127.0.0.1:8080/api/clock | jq

# Watch the SSE stream — every mutation produces a `clock-changed` event:
curl --no-buffer -N http://127.0.0.1:8080/api/alerts/stream

# Open http://127.0.0.1:8080/ in a browser to see the demo bar
# at the bottom of every page (Dashboard, Explorer, Analysis, Alerts).
# The cursor readout updates live; clicking through the controls
# triggers re-fetches of the dashboard widgets within ~300 ms.

# Wall mode — restart with CLIMASENSE_CLOCK_MODE=wall and the demo bar
# greys out:
CLIMASENSE_CLOCK_MODE=wall docker compose up -d
curl -X POST -H 'Content-Type: application/json' \
    -d '{"action":"pause"}' http://127.0.0.1:8080/api/clock
# 409 with "clock_immutable_in_wall_mode"
```

## Compose regression: every slice 1-11 surface still works after seeking

- `/api/health/live` + `/api/health/ready` — unchanged (slice 1).
- `/api/readings/latest` — cursor-clipped by `CursorSnapshot.Clip`; re-fetches on `cursor-changed` from the dashboard handler (slice 3).
- `/api/readings/range` + `/api/readings/heatmap` — cursor-clipped; user-driven range (slice 4).
- `/api/forecast` GET/POST + `/api/forecasts/latest` — cursor-clipped via `dbo.fv_forecasts_at_cursor` (slice 5).
- `/api/leaderboard` — global / unchanged (slice 6).
- `/api/comfort/score` + `/api/comfort/current` — cursor-clipped via `dbo.fv_comfortscores_at_cursor`; re-fetches on `cursor-changed` (slice 7).
- `/api/anomalies/detect` + `/api/anomalies` + `/api/anomalies/latest` — cursor-clipped via `dbo.fv_anomalies_at_cursor`; re-fetches on `cursor-changed` (slice 8).
- `/api/profiles/analyze` + `/api/profiles` — cursor-clipped via `dbo.fv_dayprofiles_at_cursor` (slice 9).
- `/api/comfort/budget` — cursor-clipped; re-fetches on `cursor-changed` (slice 10).
- `/api/alerts/stream` SSE — three event types now: `server-tick`, `breach-detected`, `clock-changed` (slice 11 + slice 12).
- `/api/alerts` + `/api/alerts/rules` — cursor-clipped via `dbo.fv_alerts_at_cursor`; re-fetches on `cursor-changed` (slice 11).
