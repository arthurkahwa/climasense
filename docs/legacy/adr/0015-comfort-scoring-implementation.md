# 15. Comfort scoring implementation (slice 7 — amends ADR-0005)

Date: 2026-05-17
Status: Accepted

## Context

ADR-0005 pins the ASHRAE-55 graphical comfort zone as the scoring
approach. Slice 7 (#9) lands the implementation. The PRD (#2) tightened
ADR-0005 with three additions that this ADR records explicitly:

1. A `Season` column on `dbo.ComfortScores` (`'summer'` / `'winter'`).
2. A `COMFORT_HEMISPHERE` environment variable (default `N`) that
   selects which calendar months map to which season.
3. The scoring function is **pure** — no DB, no clock dependency,
   no IO. Composition with the SQL-backed trailing-window mean and
   the persistence layer happens outside the calculator.

## Decision

### Module layout

The implementation lives in four modules with explicit boundaries:

- `climasense_ml/comfort.py` — pure scorer. `ComfortCalculator.score()`
  takes `(t_c, rh_pct, bucket_time, hemisphere)` and returns
  `ComfortScoreResult(score, rating, season)`. Hardcoded polygon
  vertices for summer + winter. Point-in-polygon (ray-cast) + Euclidean
  distance-to-segment for points outside.
- `climasense_ml/comfort_persistence.py` — MERGE-based upsert on
  `BucketTime`; read helpers go through
  `dbo.fv_comfortscores_at_cursor(@asOf)`.
- `climasense_ml/comfort_emitter.py` — composition: SQL trailing-hour
  mean + pure scorer + MERGE upsert. Includes `ComfortEmitter` with
  the β-prime gate (slice-5 pattern).
- `climasense_ml/comfort_router.py` — FastAPI handler for
  `GET /api/comfort/score`. Recomputes and MERGEs on every call;
  idempotent on `(BucketTime)`.

### Hemisphere mapping

Per issue #9 spec:

| Month  | Northern hemisphere | Southern hemisphere |
|--------|---------------------|---------------------|
| Apr–Oct | summer              | winter              |
| Nov–Mar | winter              | summer              |

Selected at process start via `COMFORT_HEMISPHERE` (default `"N"`;
accepted variants are case-insensitive `N`/`S`/`North`/`South`/
`Northern`/`Southern`). Changing the env var requires a process
restart.

### Polygon vertices (T °C × RH %)

Both polygons are listed clockwise from the lower-left corner.

**Summer** (clo ≈ 0.5, met ≈ 1.2, still air):
`(24.5, 0) (27.5, 0) (26.5, 60) (25.5, 80) (22.5, 80) (23.5, 60)`

**Winter** (clo ≈ 1.0, met ≈ 1.2, still air):
`(20.0, 0) (23.5, 0) (23.0, 60) (22.0, 80) (19.0, 80) (19.5, 60)`

These are derived from ASHRAE 55-2020 Figure 5.3.1 and rounded to
0.5 °C in `T` and 5 % in `RH`. Air temperature approximates
operative temperature (no mean-radiant or air-velocity inputs —
see ADR-0005).

### Scoring formula

```
score = 100                              if (T, RH) inside polygon
      = max(0, 100 - α · d_polygon)      otherwise
```

`α = 4.0`. The constant lands such that:
- Distance ≈ 2.5 units → "excellent" (≥ 90).
- Distance ≈ 7.5 units → "acceptable" (≥ 70).
- Distance ≈ 12.5 units → "marginal" (≥ 50).
- Distance ≥ 25 units → "uncomfortable" (0).

Rating bands per ADR-0005:
`excellent ≥ 90 > acceptable ≥ 70 > marginal ≥ 50 > uncomfortable`.

### Scheduling

`ComfortEmitter` is registered with APScheduler in `main.py` (chained
after the leaderboard seeder). Fires every wall-minute; the β-prime
gate inside `emit_if_due()` opens only when
`snap.as_of - last_bucket >= 1 h`. The window the score is computed
against is the trailing 1 hour of `SensorReadings` (mean `T` + mean
`RH`).

### Read path

`GET /api/comfort/current` on the .NET tier reads
`dbo.fv_comfortscores_at_cursor(@asOf)` directly (read-path bypass per
ADR-0010). The dashboard's comfort card hydrates from a single
`fetch('/api/comfort/current')`.

`POST /api/ml/run/comfort` on the .NET tier proxies to the ml-tier
`GET /api/comfort/score?hours=24` (per issue #9 spec) — the proxy is
a thin pass-through that surfaces the envelope (and the persistence
side-effect) verbatim. Idempotent on `(BucketTime)`.

## Consequences

- The pure scorer is locked by golden test 2
  (`test_comfort_polygons_seasonal_boundary`) which exercises both
  polygons (centroid + ≥3 interior points each), the Apr/May and
  Oct/Nov seasonal boundaries in the north, and the southern-
  hemisphere mirror.
- Polygon vertex changes are an explicit ADR decision; the golden
  test fails loudly when vertices drift.
- The Apr→Oct vs Nov→Mar split puts April on the summer side (per
  issue #9 spec); the explicit seasonal boundary occurs at Oct/Nov.
- `α = 4.0` is a tuning constant. If reviewers find the rating bands
  too lenient or too strict, the constant — not the polygons — is
  the knob to turn.
- Future enhancement: predicted-comfort (forecast (T, RH) → forecast
  comfort) is explicitly out of scope per PRD §"Out of Scope".
