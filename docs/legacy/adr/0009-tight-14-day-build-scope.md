# 9. Tight 14-day build scope — one live forecaster, leaderboard from notebook

Date: 2026-05-08
Status: Accepted

## Context

After ADR-0001 through ADR-0008, the build path includes: `IForecaster` + `LagLinearForecaster`, three-detector anomaly pipeline, calendar-conditioned profiles, ASHRAE comfort polygon, Comfort Budget panel, replay clock with `IClock` propagated through .NET and Python, SSE alert delivery, plus the original ASP.NET + FastAPI + SQL Server scaffolding. The Days 8–10 budget in the README ("ARIMA, Isolation Forest, K-Means, ASHRAE comfort, APScheduler") is now stale — three of those line items were dropped or replaced and several new pieces of work were introduced.

Implementing all five forecasters live in 14 days is unrealistic for one person.

## Decision

Adopt a **tight scope** for the 14-day build:

- **One live forecaster**: `LagLinearForecaster` (the empirical winner from the notebook).
- **Leaderboard UI**: populated from notebook-evaluation rows for ARIMA, SARIMA, Holt-Winters, LSTM, 1D-CNN. Static historical evaluation, displayed alongside the live model so reviewers see the receipts.
- **`IForecaster` interface in place**: alternates can be added in future as one class per implementation.

Revised 14-day plan:

| Day | Work |
|---|---|
| 1–2 | docker compose, init-db.sql, import-data.sql, IClock skeleton |
| 3–4 | ASP.NET API, EF Core, range/heatmap/latest endpoints |
| 5–6 | Dashboard + Explorer (read-only) |
| 7 | FastAPI scaffold + LagLinearForecaster + IForecaster interface + persistence |
| 8 | ASHRAE comfort polygon + scheduled job + ComfortScores table |
| 9 | Three-detector anomaly pipeline (rules + changepoint + residual) |
| 10 | Calendar-conditioned profiles + DayProfiles SQL views |
| 11 | Threshold alert engine + SSE wiring + Alert history page |
| 12 | Comfort Budget panel + leaderboard UI (notebook-seeded) |
| 13 | Replay clock cursor + demo controls + integration |
| 14 | Polish, README rewrite, demo walkthrough |

## Consequences

- Zero slack in the schedule. Every day is loaded; one bad day pushes the demo walkthrough.
- The README must explicitly declare the production-vs-evaluated split and link to the notebook leaderboard.
- The portfolio pitch loses the ability to claim "five forecasters in production" but gains "one forecaster in production, five evaluated, here's the receipts" — a stronger framing for engineering reviewers.
- Adding any of the four evaluated forecasters live is a future ADR + one class implementing `IForecaster`.

## Amendment — 2026-05-20 (post-slice-13)

The 14-day build shipped 2026-05-08 → 2026-05-20 (13 implementation slices + a slice-0 setup commit). The day-by-day plan above is superseded by the slice table actually delivered:

| Slice | Day | What landed |
|---|---|---|
| 1 | 1–2 | `docker-compose.yml`, `init-db.sql` (SQL-first schema authority), upstream-view env vars, `IClock` skeleton, two-tier healthchecks, structured JSON logs + `X-Request-ID`, `AlertStream` SSE singleton + heartbeat |
| 2 | — | `contracts/openapi.yaml` as the wire-format source of truth + Kiota + `datamodel-codegen` codegen on both sides + `ContractValidator` startup assertion |
| 3 | — | `IngestionService.bootstrap_from_csv_if_empty()` via `bcp` + bootstrap-aware `/api/health/ready` + `/api/readings/latest` |
| 4 | 3–4 | Historical Explorer — `/api/readings/range`, `/api/readings/heatmap`, Plotly chart, range selector |
| 5 | — | `LagLinearForecaster` boot-fit + `LeaderboardSeeder` + `/api/forecasts/latest` + golden test 1 |
| 6 | — | Analysis page (notebook leaderboard + live row) + golden test 5 |
| 7 | — | `ComfortCalculator` + ASHRAE polygon + `Season` column + golden test 2 |
| 8 | 9 | Three-detector anomaly pipeline + golden tests 3 + 4 + per-type idempotency |
| 9 | 10 | Calendar-conditioned `DayProfiles` + Pattern CASE with notebook-derived thresholds |
| 10 | 12 | Comfort Budget panel — hours-outside-zone + worst calendar cell + 7-day trend |
| 11 | 11 | Threshold alert engine + Alert history page + `breach-detected` SSE event type |
| 12 | 13 | `ReplayClock` + `dbo.ReplayState` + demo controls UI + `clock-changed` SSE event type |
| 13 | 14 | This slice — CI smoke test + README walk-backs + ADR audit |

Two walk-backs to the original 2026-05-08 plan:

1. **The `IForecaster` "scaffold for future" reasoning above is walked back.** Per ADR-0011 (CursorSnapshot + interface emergence policy) and ADR-0017 (test affordances + interface emergence), no interface was extracted from a single concrete class. `LagLinearForecaster` ships as a concrete class. When the second forecaster arrives, the interface is extracted from the two concrete shapes — not speculated from one. The "one class implementing `IForecaster`" framing in the Consequences above is superseded.
2. **AlertRules CRUD UI was deferred.** Rules are seeded in `init-db.sql`; the dashboard reads `AlertRules` and renders rule summaries on each alert row but does not let reviewers add / edit / disable rules. A future ADR (no number yet) covers the CRUD surface.

The SSE infrastructure that slices 11 and 12 depend on was pre-built on slice 1, not on Day 11 — see ADR-0007's amendment.
