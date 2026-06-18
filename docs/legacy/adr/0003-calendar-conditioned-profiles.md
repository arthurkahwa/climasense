# 3. Calendar-conditioned profiles (K-Means clustering dropped)

Date: 2026-05-08
Status: Accepted

## Context

The original spec promised K-Means daily clustering with labels like "warm weekday" and "cool weekend" via `models/clusterer.py`. Section 8 of `Climate_Time_Series_Analysis.ipynb` directly disconfirms this: the LSTM hidden-state PCA shows weekday and weekend daily profiles projected on top of each other, with the notebook noting "no obvious 'warm weekday vs cool weekend' cluster, mirroring what the EDA already hinted at".

Shipping K-Means anyway would either produce meaningless cluster labels or require post-hoc relabeling that masks the absence of structure.

## Decision

Replace K-Means with **calendar-conditioned z-score profiles**. For each `(day_of_week, hour_of_day)` cell, compute the historical mean and standard deviation. For any specific day, the "profile" is its z-score against its calendar-matched cohort.

Schema: rename `DayClusters` → `DayProfiles`. Columns: `Date`, `DayOfWeek`, `MeanResidual`, `MaxAbsZscore`, `Pattern ∈ {quiet, warm, cool, volatile}`. `Pattern` is the deterministic output of a SQL CASE expression on the z-scores, not a model fit.

## Consequences

- `models/clusterer.py` is deleted. Net code reduction.
- "Three distinct ML techniques" is no longer a true claim. README's AI/ML framing rewrites accordingly.
- `DayProfiles` recomputation is a SQL query, not a scheduled fit — APScheduler's nightly clustering job becomes a `REFRESH MATERIALIZED VIEW` (or equivalent SQL Server pattern).
- The Explorer "cluster view" becomes a "profile view" — same UI structure, different data source.
- The "Recommendations engine driven by historical clusters" feature loses its input (see ADR-0006).

## Amendment — 2026-05-20 (post-slice-13)

Slice 10 made the `Pattern` CASE expression concrete with notebook provenance + a stable precedence rule:

- **Thresholds.** `Pattern` is decided by p25 / p75 of `MeanResidual` (split warm vs cool) plus p90 of `MaxAbsZscore` (volatile gate). The exact numeric values live in `scripts/init-db.sql` with an inline citation block pointing to the notebook cells in §8 that produced them. Derivation script: `scripts/derive_pattern_thresholds.py` (idempotent — re-running on the same notebook produces the same constants).
- **Precedence.** Where two conditions overlap, the rule is `volatile > warm > cool > quiet`. A high-variance day with a positive mean residual labels `volatile`, not `warm`. This is encoded in the order of CASE branches in `init-db.sql`. Locked by the slice-10 profile-router tests.
- The "REFRESH MATERIALIZED VIEW (or equivalent)" speculation above is superseded by a per-day INSERT pattern: profiles are computed at the boundary of the previous replay-day and inserted with `dbo.fv_dayprofiles_at_cursor(@asOf)` providing the read-time cursor clip (per ADR-0011's TVF strategy).
