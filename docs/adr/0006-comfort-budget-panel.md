# 6. Comfort Budget panel (recommendations engine dropped)

Date: 2026-05-08
Status: Accepted

## Context

The original spec listed a "Recommendations engine | Simulated HVAC optimization tips driven by historical clusters". Two things broke this:

1. ADR-0003 dropped K-Means, so the input ("historical clusters") no longer exists.
2. The word "Simulated" was already doing significant work — the recommendations were never grounded in real HVAC behaviour or any ground-truth feedback loop.

A made-up tips card in a portfolio is more dangerous than absent — a careful reviewer reads "Simulated HVAC optimization" and discounts the rest of the README.

## Decision

Drop the recommendations engine. Replace its UI slot with a **Comfort Budget** panel — a deterministic readout computed from real data. Three cards:

1. **Hours outside zone** — count of hours in the past 7 days where ASHRAE comfort score (ADR-0005) < 70.
2. **Worst calendar cell** — the (day_of_week, hour) cell from `DayProfiles` (ADR-0003) with the most-negative mean residual this week, e.g. "Mon 06:00, z = −1.8".
3. **Comfort trend** — 7-day min / max / mean of the comfort score with a sparkline.

All three are pure SQL aggregations over the `ComfortScores` and `DayProfiles` tables. No ML, no inference, no "Simulated" anything.

## Consequences

- The README's "Cost narrative — Estimated energy/risk savings" line dies with the recommendations engine. It was never honest.
- The Alerts and Recommendations section reduces to two cards (Threshold alerts + Comfort Budget) instead of four.
- Code cost: three SQL queries, one Razor page section, one Plotly sparkline. Net code reduction vs the original recommendations engine.
- Portfolio framing strengthens: "deterministic facts about the data" reads better than "simulated tips" for an engineering audience.
