# 5. ASHRAE 55 graphical comfort zone (PMV/PPD rejected)

Date: 2026-05-08
Status: Accepted

## Context

The original spec named "ASHRAE heuristic" as the comfort-scoring approach. ASHRAE 55 PMV/PPD (the standard quantitative model) requires six inputs:

1. Air temperature
2. Mean radiant temperature
3. Air velocity
4. Relative humidity
5. Metabolic rate
6. Clothing insulation

The dataset has **two** of these (T, RH). A PMV implementation would need defaults for the other four, and those defaults would dominate the score — fabricating ~75% of the input.

## Decision

Implement the **ASHRAE 55 graphical comfort zone** (Standard 55-2020 Figure 5.3.1) instead. Two psychrometric polygons hard-coded from the standard:

- **Summer** (clo ≈ 0.5, met ≈ 1.2, v assumed still)
- **Winter** (clo ≈ 1.0, met ≈ 1.2, v assumed still)

Season selection: month-of-year mapping (May–September → summer, otherwise winter). Score:

```
score = 100                              if (T, RH) inside polygon
      = max(0, 100 − α · d_polygon)      otherwise
```

Where `d_polygon` is Euclidean distance to the polygon boundary in (T, RH) space. `Rating ∈ {excellent (90+), acceptable (70–89), marginal (50–69), uncomfortable (<50)}`.

API: existing `GET /api/comfort/score?hours=24` works unchanged.

## Consequences

- The "ASHRAE 55" badge in the README remains defensible — graphical zone evaluation is a real, documented part of the standard.
- The README must declare the omitted PMV inputs honestly: "PMV/PPD not implemented because radiant temperature and air velocity are not measured; assumed defaults would let the assumptions dominate the score."
- `comfort.py` becomes a small module — two polygon definitions plus a point-in-polygon and signed-distance routine. No fitting, no model file.
- ADR-0006 (Comfort Budget) consumes this score directly.
- Future enhancement (adaptive comfort, ASHRAE 55-2020 Section 5.4) requires outdoor-temperature data which we don't have — out of scope.

## Amendment — 2026-05-20 (post-slice-13)

Three corrections from slice 7:

1. **`Season` is a stored column on `ComfortScores`**, not a derived field. Persisted at every emit so the audit trail is in the data, not in code. Values are exactly `'summer' | 'winter'`.
2. **`COMFORT_HEMISPHERE` env var** (default `N`) drives season selection so the platform works in both hemispheres. Northern: May–September → summer. Southern: November–March → summer. Locked by `test_comfort_polygons_seasonal_boundary` (golden test 2).
3. **Air temperature is used as a proxy for operative temperature.** This deviation from the standard is disclosed in the README's Comfort scoring row and in `comfort.py`'s module docstring. The simplification holds in low-radiant-asymmetry indoor environments — which this dataset is.

The original "API surface unchanged" line was technically true but understated the slice 7 work: the response shape gained `season` and `hemisphere` fields (still served by `GET /api/comfort/current` and `GET /api/comfort/score?hours=24`).
