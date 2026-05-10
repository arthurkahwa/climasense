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
