# 7. Threshold alerts on the replay clock, delivered via DB + SSE

Date: 2026-05-08
Status: Accepted

## Context

ADR-0004 introduced two clocks (replay cursor and wall-clock). The threshold alert engine ("Alert if T > X for Y minutes") needed to commit to one. The README also did not specify alert delivery — only that history is "persisted" — leaving the question of whether breaches surface in real time during a demo unanswered.

A breach that lasts 2 hours of replay time should produce **one** alert row, not one per polling tick — required for correct alert history and meaningful dedup.

## Decision

Three coupled rules:

1. **Clock**: alerts fire on `IClock.Now()` from ADR-0004 — i.e. the replay cursor. Wall-clock alerts never fire because no live data ever arrives in Replay mode.
2. **Idempotency**: a `Breach` is a `(rule_id, started_at, ended_at)` interval, computed by a SQL window function over the relevant aggregated readings. One `Alert` row per breach interval. The engine queries for new breaches each tick — it never inspects individual readings.
3. **Delivery**: DB-only persistence + SSE (`EventSource`) to open dashboard tabs. ASP.NET emits a `breach-detected` event on a `/api/alerts/stream` endpoint. No email, no SMS, no push — explicitly out of scope.

Schema:

- `AlertRules`: `RuleId, Metric (T|RH), Operator (>|<), Threshold, DurationMinutes, Enabled, CreatedAt`.
- `Alerts`: `AlertId, RuleId, BreachStart, BreachEnd, PeakValue, ReplayClockAtFire`. The `ReplayClockAtFire` column makes the clock-source explicit on every row.

## Consequences

- Alerts are demoable in the same `docker compose up` walkthrough that exercises forecasts, anomalies, and comfort scoring.
- The Alert history page shows persistent records keyed by replay-clock time, which matches the rest of the dashboard's time axis.
- SSE wiring is one new ASP.NET endpoint and one `EventSource` subscriber per dashboard; minimal frontend work.
- A future production deployment with `WallClock` would surface alerts identically — only the data source changes.
- README must declare "browser-only delivery; integration with email/SMS/push is intentionally out of scope".

## Amendment — 2026-05-20 (post-slice-13)

Three corrections from slice 11 + the SSE pre-build in slice 1:

1. **Closure-only delivery.** An `Alerts` row is written *only* when a breach interval has closed — never while a breach is in progress. The threshold engine fires every wall-minute and evaluates `[replay_now - 24h, replay_now]` via per-rule gaps-and-islands SQL over `SensorReadings`. Currently-open breaches produce zero rows. This makes the dashboard's "Last alert" widget a terminal-event indicator, not a real-time tracker. See CONTEXT.md → "Closure-only delivery."
2. **24-hour lookback + `UNIQUE(RuleId, BreachStart)` idempotency.** The engine re-evaluates a sliding 24-hour window every minute; `INSERT … WHERE NOT EXISTS` against the unique key dedups across overlapping evaluations. No row is written twice for the same breach.
3. **SSE infrastructure was pre-built on Day 6 / slice 1.** `AlertStream` singleton + heartbeat (`server-tick`, 15 s) + `Last-Event-ID` reconnection landed before any alert logic existed. Slice 11 added the `breach-detected` event type to that channel. Slice 12 added the `clock-changed` event type. Three event types share one channel, one monotonic id-space, one reconnect contract. Justifies the original ADR-0009's "alert delivery is small wiring work" claim.

`AlertRules` rows are seeded in `init-db.sql` (`T > 26 °C for 30 min`, `RH > 70 % for 60 min`, `T < 18 °C for 60 min`). Live CRUD UI is out of scope — see ADR-0009's amendment.
