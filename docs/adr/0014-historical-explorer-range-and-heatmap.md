# ADR-0014 — Historical Explorer: range + heatmap shape

> Status: accepted (slice 4 / 2026-05-17).
> Supersedes nothing. Builds on ADR-0011 (interface emergence policy)
> and ADR-0013 (bcp bootstrap, which guarantees `SensorReadings` is
> populated by the time the Explorer page loads).

## Context

PRD #2 promises a historical Explorer page where reviewers can pan /
zoom / aggregate any portion of the 10-year (2019–2026) sensor history.
Three concrete capabilities need to land before reviewers can exercise
the corpus interactively:

1. A **range** endpoint that accepts an arbitrary `(start, end, bucket)`
   triple and returns aggregated rows. `bucket` covers raw (no
   aggregation), hourly, daily, and weekly — wider than a single
   width because the same page must render both a 1-day raw view
   (~288 rows) and a 1-year weekly view (~52 rows).
2. A **heatmap** endpoint that returns one cell per calendar day for a
   selected year — the GitHub-contribution-style overview that
   reviewers can scrub through. Dense (365/366 cells) so the
   dashboard never has to do its own gap-filling.
3. A small UI module that wires the two endpoints to Plotly.js. No
   framework; vanilla JS + `fetch`.

Three load-bearing design choices were settled in slice 4:

### A. SQL aggregation via `DATE_BUCKET`

SQL Server 2022 ships the `DATE_BUCKET` function — a true bucketing
operator that accepts a fixed-width datepart (HOUR, DAY, WEEK), a
multiplier (always 1 in this slice), an instant column, and an optional
origin. The aggregated `SELECT … GROUP BY DATE_BUCKET(…) ORDER BY …`
keeps the response set bounded:

| Bucket | Worst case (10 years, 1-min resolution) |
|---|---|
| `week` | ~520 rows |
| `day` | ~3650 rows |
| `hour` | ~87 600 rows |
| `raw` | ~5.2 M rows |

`raw` would blow the response. Slice 4 caps raw requests at
`CLIMASENSE_RAW_MAX_DAYS` (default **7**, equivalent to ~10 080 raw
rows) and returns `400 range_too_large` for wider windows.

### B. Endpoints bypass the ml tier

Both endpoints live on the .NET tier and read SQL directly, per
ADR-0010's read-path-bypass rule. The Pydantic side of the contract
does NOT serve these endpoints; the ContractValidator's
`_ML_TIER_EXCLUDED_PATHS` set was extended to skip them when comparing
FastAPI's emission to the YAML. The Kiota client (in
`Generated/MLClient/`) would gain matching request builders on the
next regeneration, but since nothing on the .NET side calls these via
Kiota (the web tier serves them, not consumes them) their absence is
benign and the build is unaffected.

### C. CDN-loaded Plotly + a tiny shared theme module

Plotly.js minified is ~3 MB. Two options were considered:

* **CDN** (`https://cdn.plot.ly/plotly-2.x.min.js`) — zero build
  complexity, requires a single network hit on first page load.
* **Vendor** to `wwwroot/lib/plotly.min.js` — air-gapped, but bloats
  the repo by ~3 MB without a portfolio benefit.

The portfolio demo's reviewers are assumed online. Slice 4 ships the
CDN load with a documented fall-back: vendoring is one file rename +
one `<script src>` change. The shared dark-theme defaults live in
`wwwroot/js/plotly-config.js`; per-page logic (Explorer, plus future
Comfort / Leaderboard pages) imports `ClimaSensePlotly.darkLayout()`
and `darkConfig()`.

## Decision

* **API surface**:
  * `GET /api/readings/range?start=...&end=...&bucket=raw|hour|day|week`
    returns a `BucketedReadingsResponse` with a dense `buckets` array
    for aggregated requests (gaps filled with `sampleCount: 0` and
    `null` metric fields), or pass-through rows for `bucket=raw`.
  * `GET /api/readings/heatmap?year=YYYY` returns a `HeatmapResponse`
    with exactly 365 or 366 dense `cells`.
  * Both endpoints cursor-clip via `WHERE ReadingTime <= @asOf`.

* **Service layer**: `RangeQueryService` is a concrete class with two
  delegate seams — `RangeFetcher` and `HeatmapFetcher` — following the
  slice-3 `SensorDataService` pattern. The seams are parameterised
  with `Func`-shaped delegates so tests inject lambdas; production
  wires them to `SqlRangeFetcher.FetchRangeAsync` and
  `FetchHeatmapAsync` respectively. No speculative interfaces, per
  ADR-0011.

* **Bucket alignment**: SQL Server's `DATE_BUCKET(HOUR, 1, x)` /
  `DATE_BUCKET(DAY, 1, x)` / `DATE_BUCKET(WEEK, 1, x)` aligns on the
  default `1900-01-01` origin (a Monday). The .NET densification
  routine in `RangeQueryService.AlignDown` mirrors that alignment —
  HOUR rounds to the hour, DAY to midnight, WEEK to the previous
  Monday at 00:00 UTC. The `AlignDown_week_lands_on_monday` test pins
  the contract.

* **Validation outcomes** are returned as an enum
  (`RangeQueryError.{None,StartAfterEnd,RawWindowTooLarge}`) so the
  endpoint handler maps them to canonical `400 ProblemDetails` bodies
  without throwing. The handler also rejects unparseable timestamps
  and unknown bucket literals before reaching the service.

* **UI**: `Pages/Explorer.cshtml` is a single Razor page hosting two
  Plotly chart divs (`#explorer-timeseries` + `#explorer-heatmap`).
  `wwwroot/js/explorer.js` wires range/bucket buttons + a
  `<input type="datetime-local">` custom picker. The page state lives
  in the URL hash so reloads preserve the view.

## Consequences

**Positive**

* Reviewers can scrub any range / aggregation interactively. The
  10-year ALL view at `bucket=week` returns ~520 buckets and renders
  in well under a second.
* The min/max envelope band on the time-series chart visually
  communicates within-bucket variance — a fact the notebook's static
  figures cannot.
* The heatmap surfaces seasonal patterns and bootstrap-incomplete days
  at a glance (zero-sample days render as dark cells, populated days
  follow the colour scale).
* The cursor-clip is structural: the SQL `WHERE ReadingTime <= @asOf`
  binds the cursor on every query, so slice 12's `ReplayClock` lands
  with no further work on these endpoints.
* The 7-day raw cap is a bounded-response guarantee. A future ADR can
  relax it if the dashboard adds streaming JSON or chunked responses.

**Negative**

* The 7-day raw cap may surprise a reviewer who clicks Raw on a
  1-month window. The 400 body's `message` names the cap explicitly
  and suggests the appropriate aggregation; the dashboard's flash
  toast surfaces it.
* CDN load adds a single external dependency for the Explorer page.
  Vendoring is a documented mechanical swap.
* `DATE_BUCKET` requires SQL Server 2022+. ClimaSense already targets
  2022-latest; this ADR locks the dependency.
* `RangeQueryService` densifies aggregated buckets in .NET (not SQL).
  An alternative — `GENERATE_SERIES` + LEFT JOIN in SQL — would push
  the work down to the DB, but the response set sizes are small
  enough (~8 760 buckets max for `hour` over a year) that the .NET
  pass is well under 100 ms. We may revisit if profiling shows
  otherwise.

## Future ADRs that this decision invites

* When a streaming-JSON or chunked-response path lands (e.g. for raw
  multi-month exports), an ADR will pin the back-pressure shape.
* When the dashboard's auto-refresh story arrives (slice 12 via SSE
  `clock-changed`), the Explorer's behaviour on cursor moves needs to
  be settled — does it re-fetch on a seek, or hold the user's window?
