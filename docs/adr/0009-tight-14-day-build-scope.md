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
