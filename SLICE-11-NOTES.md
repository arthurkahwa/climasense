# Slice 11 implementation notes (threshold alert engine + Alert history + breach-detected SSE)

> Auxiliary working-notes for slice 11 (issue #13). Reviewer-facing
> map of what landed and how to validate it.

## What landed

| Concern | Path | Notes |
|---|---|---|
| Alert rule POCO | `src/ClimaSense.Web/Alerts/AlertRule.cs` | Mirrors one `dbo.AlertRules` row. `MetricColumn` / `SqlOperator` / `PeakAggregate` whitelist the only allowed values so the gaps-and-islands SQL template carries no caller-controlled strings. `Summary` renders the canonical `"T > 26 °C for 30 min"` string used by the wire, the toast, and the history table. |
| Threshold engine core | `src/ClimaSense.Web/Alerts/AlertScanService.cs` | Concrete (no `IAlertScanService` interface, per ADR-0011). Three delegate seams (`AlertRulesLoader` / `RuleBreachScanner` / `AlertInserter`) so tests swap lambdas. `ScanOnceAsync` reads the cursor once, iterates enabled rules, runs each rule's gaps-and-islands query, and emits one `NewAlert` per insertion that landed. `RenderGapsAndIslandsSql` is `public static` so `AlertScanServiceTests` can golden-string-lock the closure-only filter, the duration filter, and the 5-minute gap-split. |
| SQL adapter for the engine | `src/ClimaSense.Web/Alerts/SqlAlertScanner.cs` | Three public `const string` SQL operations: `LoadRulesSql` (enabled rules), the rendered gaps-and-islands per rule, and `InsertAlertSql` (the idempotent `INSERT ... WHERE NOT EXISTS` + `OUTPUT INSERTED.AlertId INTO @out`). All parameters bound via `SqlCommand.Parameters` — no string concatenation of caller input. |
| Alert history read service | `src/ClimaSense.Web/Alerts/AlertReadService.cs` | Concrete; one delegate seam (`AlertHistoryFetcher`). `limit` is clamped to `[1, 200]`; default 50. |
| SQL adapter for history | `src/ClimaSense.Web/Alerts/SqlAlertReader.cs` | `public const string HistorySql` joins `dbo.fv_alerts_at_cursor(@asOf)` against `dbo.AlertRules`. Renders the rule summary on the .NET side so both `/api/alerts` rows and the SSE `breach-detected` payload use the same one source of truth (`AlertRule.Summary`). |
| Alert rules read service | `src/ClimaSense.Web/Alerts/AlertRuleReadService.cs` | Concrete; one delegate seam (`EnabledRulesFetcher`). Wraps each `AlertRule` POCO in an `AlertRuleRowDto` for the wire (adds the computed `summary` field). |
| BackgroundService | `src/ClimaSense.Web/Alerts/ThresholdAlertScanner.cs` | `BackgroundService` firing every 60 wall-seconds via `PeriodicTimer` (the .NET-equivalent of APScheduler's `interval` job). Captures a `CursorSnapshot` per tick, delegates the scan to `AlertScanService.ScanOnceAsync` from a fresh DI scope, then broadcasts one SSE `event: breach-detected` per inserted row via the slice-1 `AlertStream`. Initial delay tunable via `CLIMASENSE_ALERT_SCAN_INITIAL_DELAY_SECONDS` (default 30 s). Inner exceptions are logged + swallowed so a transient DB outage doesn't take the BackgroundService down. |
| DTOs | `src/ClimaSense.Web/Alerts/AlertDtos.cs` | `AlertRowDto`, `AlertHistoryResponseDto`, `AlertRuleRowDto`, `AlertRulesResponseDto`, `BreachDetectedPayload`. Hand-rolled DTOs (not Kiota POCOs) so the global `JsonNamingPolicy.CamelCase` applies (same rationale as slices 5–10). |
| Web endpoints | `src/ClimaSense.Web/Program.cs` | `GET /api/alerts?limit=N` (cursor-clipped history, clamped to 1..200) and `GET /api/alerts/rules` (enabled rules). DI adds `SqlAlertReader` + `SqlAlertScanner` singletons, three scoped services, and the `ThresholdAlertScanner` hosted service. The hosted service is wired as `AddSingleton<ThresholdAlertScanner>` + `AddHostedService(sp => sp.GetRequiredService<...>())` so the initial-delay config knob can be read at registration time. |
| Alert history page | `src/ClimaSense.Web/Pages/Alerts.cshtml` (+ `.cs`) | Pure-render PageModel. Hydrates from `fetch('/api/alerts?limit=200')` + `fetch('/api/alerts/rules')`. Subscribes to `/api/alerts/stream` and listens for `event: breach-detected` to (a) toast the new alert and (b) prepend the row to the history table. Tracks rendered `alertId`s to avoid duplicating rows when an initial fetch races a live event. textContent-only rendering for injection safety (same posture as slices 3, 7, 8, 10). |
| Dashboard breach-toasts | `src/ClimaSense.Web/Pages/Index.cshtml` | New nav link to `/Alerts`; new `#toast-stack` div + CSS; new `breach-detected` listener piggybacked on the existing SSE EventSource (same `id:` space + `Last-Event-ID` reconnection semantics as `server-tick`). |
| AlertStream test affordance | `src/ClimaSense.Web/ClimaSense.Web.csproj` | Adds `<InternalsVisibleTo Include="ClimaSense.Web.Tests" />` so `ThresholdAlertScannerTests` can drain `Frame` records via the channel reader without spinning a real HTTP SSE client. |
| Contract | `contracts/openapi.yaml` | Adds `/api/alerts`, `/api/alerts/rules`, plus 7 schemas (`AlertMetric`, `AlertOperator`, `AlertRow`, `AlertHistoryResponse`, `AlertRuleRow`, `AlertRulesResponse`, `BreachDetectedPayload`). The existing `/api/alerts/stream` description is extended to document the slice-11 `breach-detected` event type. `info.version` bumped to `0.11.0-slice-11`. Header docstring updated. |
| Validator exclusions | `src/ClimaSense.ML/climasense_ml/contract_validator.py` | `/api/alerts`, `/api/alerts/rules` added to `_ML_TIER_EXCLUDED_PATHS`; the 7 new schemas added to `_DROP_SCHEMAS`. The ml tier doesn't reference any of these — the threshold engine + delivery path are .NET-only per ADR-0007. |
| Stub-test exclusion | `src/ClimaSense.ML/tests/test_stubs_return_501.py` | `("/api/alerts", "get")` and `("/api/alerts/rules", "get")` added to `_REAL_ENDPOINTS`. Stub-floor remains 0; `test_stub_routes_floor_is_zero_after_slice_9` continues to pass. |
| Generated Kiota client | `src/ClimaSense.Web/Generated/MLClient/Api/Alerts/Rules/`, `src/ClimaSense.Web/Generated/MLClient/Models/Alert*.cs` | Regenerated by the project's `BeforeBuild` Kiota target. Adds the new request builders and POCOs. The `BreachDetectedPayload` schema is documented in the contract but not referenced by any HTTP body, so Kiota omits it from the model tree — same behaviour we saw for SSE-only payloads in slice 1. |
| Generated Pydantic models | `src/ClimaSense.ML/climasense_ml/schemas/generated.py` | Regenerated by `scripts/regen-contracts.sh python`. New `AlertMetric`/`AlertOperator` enums + 5 Pydantic models. Unused by the ml tier (web-tier-only paths) but the regen keeps the contract↔code distance at zero per ADR-0012. |

## Acceptance criteria from issue #13

- [x] **`init-db.sql` seeds three default rules idempotently** — already done in slice 1 (`scripts/init-db.sql §5`, MERGE on `Name`). No change in slice 11.
- [x] **Threshold engine runs every wall-minute under both `WallClock` and `ReplayClock`** — `ThresholdAlertScanner.Cadence == TimeSpan.FromMinutes(1)`; `IClock` injected so the same code runs unchanged when slice 12 swaps in `ReplayClock`. Test: `ThresholdAlertScannerTests.Cadence_is_one_wall_minute`.
- [x] **A 30-minute breach produces exactly one `Alerts` row** — gaps-and-islands SQL with `HAVING DATEDIFF(MINUTE, MIN, MAX) >= @durationMinutes`; UNIQUE(RuleId, BreachStart) + WHERE NOT EXISTS for idempotency. Tests: `AlertScanServiceTests.RenderGapsAndIslandsSql_uses_duration_filter`, `AlertRuleReadServiceTests.SqlAlertScanner_insert_sql_guards_against_double_insertion`.
- [x] **Re-running over the same 24h window inserts zero new rows** — the inserter returns `null` on subsequent calls (UNIQUE held), and `AlertScanService` swallows the no-op silently. Test: `AlertScanServiceTests.ScanOnceAsync_idempotent_when_inserter_returns_null` and `ThresholdAlertScannerTests.TickOnceAsync_emits_zero_frames_on_idempotent_redetection`.
- [x] **An ongoing breach (cursor mid-run) produces zero rows** — the gaps-and-islands SQL has `AND MAX(ReadingTime) < @asOf` in the HAVING clause. Test: `AlertScanServiceTests.RenderGapsAndIslandsSql_uses_closure_only_filter`.
- [x] **Browsers receive `breach-detected` events live; toast appears within 1–2 s** — `ThresholdAlertScanner.TickOnceAsync` broadcasts immediately after the INSERT; the JS handler on `/` and `/Alerts` creates a toast on receipt. Test: `ThresholdAlertScannerTests.TickOnceAsync_emits_one_breach_detected_frame_per_inserted_row` (verifies the payload shape).
- [x] **Alert history page paginated; rule description human-readable** — `Pages/Alerts.cshtml` renders the history table with `rule.summary` rendered server-side from `AlertRule.Summary`. Limit clamped at 200; default 50.
- [x] **`Alerts` rows are append-only** — the SQL is only `INSERT ... WHERE NOT EXISTS`; no UPDATE statements exist in the slice-11 codebase.

## Architecture decisions

- **.NET tier owns the threshold engine.** Per ADR-0007 + epic #2's "Alert delivery path: ASP.NET evaluates threshold rules each tick", the engine lives on the web tier as a `BackgroundService`. The ml container is uninvolved — an ml outage doesn't stop alert detection or delivery. The `ContractValidator` exclusions document this explicitly.
- **Closure-only delivery** is the central design rule per the brief. Pinned in the gaps-and-islands SQL's `HAVING MAX(ReadingTime) < @asOf`. An interval whose last in-breach reading equals `@asOf` waits one wall-minute tick before firing — judgment call: documented in the SQL comment + `AlertScanServiceTests`.
- **Idempotency at the SQL layer.** The `UNIQUE (RuleId, BreachStart)` constraint from slice 1 + `INSERT ... WHERE NOT EXISTS` make re-detection a silent no-op. We deliberately do NOT trap a duplicate-key exception — the production code path never throws on the happy path.
- **Same SSE channel for `server-tick` and `breach-detected`.** Both share the slice-1 `AlertStream` singleton, so they share the monotonic `id:` space and `Last-Event-ID` reconnection. The dashboard's existing EventSource needs only one `addEventListener('breach-detected', ...)` call.
- **One source of truth for rule summaries.** `AlertRule.Summary` is computed both at history-read time (`SqlAlertReader`) and at insertion time (`ThresholdAlertScanner`'s SSE payload). Tests lock the canonical shape against the three seeded defaults.

## Judgment calls

- **Initial delay defaults to 30 s.** The brief doesn't specify; I picked 30 s so a fresh `docker compose up` doesn't immediately try to query an empty `SensorReadings` table mid-bootstrap. Configurable via `CLIMASENSE_ALERT_SCAN_INITIAL_DELAY_SECONDS` (clamped 0..3600). The first tick runs immediately after the initial delay (not after a second cadence period), so demos see results in 30 s + scan duration.
- **History `limit` default 50, max 200.** The brief mentions "Server-side pagination (20 rows per page)" for the Razor page; I chose 50/200 for the JSON API (the page fetches 200 once and renders client-side rather than implementing offset pagination). Documented in the OpenAPI body.
- **Rule summary unit symbol.** `T` rules render with `°C`, `RH` rules with `%`. This is cosmetic-only; the wire format ships the raw string from `AlertRule.Summary`.
- **`PeakValue` aggregate**: `MAX` for `>` rules, `MIN` for `<` rules. The brief says "peak metric"; "peak" for a cold-rule is the *coldest* reading in the breach. Documented on `AlertRule.PeakAggregate`.
- **AlertStream `InternalsVisibleTo`.** Required so `ThresholdAlertScannerTests` can drain broadcast frames via the channel reader. The alternative (a public testing affordance) leaks test-only surface into production. The `InternalsVisibleTo` route is the standard .NET solution for this seam.
- **CSV symlink not created in this worktree.** The sandbox blocked `ln -s`. The compose stack assumes `sensor_data.csv` lives at the repo root; reviewers running `docker compose up` from a fresh checkout will need to ensure that file is present (the parent repo has it; the symlink only matters for worktree-local testing).

## Out of scope (per the brief)

- AlertRules CRUD UI — deferred per ADR-0011 + epic #2 ("AlertRules CRUD UI is deferred. Rules are seeded in `init-db.sql`").
- Email / SMS / push delivery — SSE-only per ADR-0007.
- Operators beyond `>` / `<` — the gaps-and-islands SQL is templated for these two; adding `>=` / `<=` / `BETWEEN` is a future ADR.
- Currently-open breach indicators — only resolved (closed) breaches surface in `dbo.Alerts` per the brief.
- A `RequestId` column on `dbo.Alerts` — flagged as a future ADR per epic #2 "Out of Scope".

## Inherited gotchas-not-reintroduced

- **No `-preview` image tags** — `Dockerfile`s unchanged from slice 10's `mcr.microsoft.com/dotnet/sdk:10.0` / `aspnet:10.0` (per slice-4 lesson).
- **`InvariantGlobalization=false` + `libicu-dev`** — unchanged.
- **Uvicorn log-config** — N/A (Python tier untouched).
- **`DateTime.TryParse` styles** — unchanged; the new endpoints use `int? limit` (model-bound) so the slice-9 `DateOnly.TryParseExact` gotcha doesn't apply.
- **Half-open intervals** — the SQL window is `[asOf - 24h, asOf]` (both inclusive on the SQL side) to match the existing slice-4/8 convention. The closure-only `MAX(ReadingTime) < @asOf` keeps the upper bound semantic from leaking into a still-open interval.
- **Public methods for test-referenced statics** — `AlertScanService.RenderGapsAndIslandsSql` + the four `*Sql` const strings on `SqlAlertReader`/`SqlAlertScanner` are all `public` so the golden-string tests can pin them.
- **ContractValidator + `_REAL_ENDPOINTS` exclusions** — both updated for `/api/alerts` and `/api/alerts/rules`. Stub-floor remains 0.

## Local validation

```bash
# .NET tests (Web)
dotnet test tests/ClimaSense.Web.Tests/ClimaSense.Web.Tests.csproj
# expected: 236 passed (was 177 after slice 10; +59 slice-11 tests)

# Python tests (ml-tier, unchanged surface)
cd src/ClimaSense.ML && uv run pytest tests/
# expected: 156 passed (unchanged from slice 10)

# Regen check (round-trip)
bash scripts/regen-contracts.sh
# expected: zero diff (codegen is idempotent on the committed contract)
```

## E2E validation (compose stack)

```bash
ln -s ../../../sensor_data.csv sensor_data.csv  # worktree-only
docker compose up -d --wait
# (4 containers healthy; bootstrap completes in ~30–60 s)

# History — should populate as the scanner ticks the historical CSV.
curl -s http://127.0.0.1:8080/api/alerts | head
curl -s http://127.0.0.1:8080/api/alerts/rules | head

# Live SSE — both heartbeat and breach-detected on the same stream.
curl --no-buffer -N http://127.0.0.1:8080/api/alerts/stream

# Idempotency: count rows, restart web, count again.
docker exec climasense-db /opt/mssql-tools/bin/sqlcmd \
    -S localhost -U sa -P "$CLIMASENSE_DB_PASSWORD" -d ClimaSense \
    -Q "SELECT COUNT(*) FROM dbo.Alerts;"
docker compose restart web
sleep 60
docker exec climasense-db ... same query ...  # should be identical
```
