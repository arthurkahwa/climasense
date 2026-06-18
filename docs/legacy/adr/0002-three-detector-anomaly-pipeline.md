# 2. Three-detector anomaly pipeline (Isolation Forest dropped)

Date: 2026-05-08
Status: Accepted

## Context

The original spec named **Isolation Forest** as the anomaly detector, with no notebook validation — Sections 5–8 of `Climate_Time_Series_Analysis.ipynb` do not evaluate it. The signal is tightly controlled (σ ≈ 1.6 °C, near-flat trend), and "anomaly" overloads at least three distinct categories of event:

1. **Sensor failures** — gaps, stuck values, runs of identical readings, physically impossible values.
2. **Regime shifts** — sensor relocations / HVAC changes (the README's overview plot already shows several).
3. **Statistical outliers** — readings far from what the model expected.

A single unsupervised detector (Isolation Forest) cannot distinguish between these and gives no honest signal of which class triggered.

## Decision

Replace the single detector with a three-detector pipeline, each matched to its category:

| Category | Detector | Cost |
|---|---|---|
| `sensor_failure` | Rule-based: `gap_minutes > 10`, `identical_run > 20`, `T ∉ [-10, 50]`, `RH ∉ [0, 100]` | SQL window functions |
| `regime_shift` | Changepoint detection on daily means via PELT (`ruptures` library), nightly | Cheap |
| `residual_outlier` | `\|y_t − ŷ_t\|` from the lag-LR forecaster (ADR-0001), severity = `\|residual\| / rolling_σ` | Free — reuses the forecaster |

Schema: `Anomalies.AnomalyType ∈ {sensor_failure, regime_shift, residual_outlier}`. `Severity` is meaningful within each type.

## Consequences

- Drops a buzzword ("Isolation Forest") from the AI/ML feature table.
- Each detector has a defensible mathematical basis and (in two of three cases) zero ML library footprint.
- Residual outliers piggyback on ADR-0001's forecaster — no new fitting infrastructure.
- The `models/anomaly_detector.py` module becomes three smaller modules or one orchestrator with three strategy classes.
- README's anomaly framing changes from "Isolation Forest → severity-scored markers" to "three-class detection with type-aware severity".

## Amendment — 2026-05-20 (post-slice-13)

Two corrections from slices 8 / 9:

1. **Per-type idempotency is a schema property, not caller discipline.** `Anomalies` carries `UNIQUE (AnomalyType, ReadingTime)`. The two point-in-time detectors (`SensorFailureRules`, `ResidualOutlierDetector`) `INSERT … WHERE NOT EXISTS` over a 24-hour scan window. `ChangepointDetector` uses scan-and-replace inside a transaction guarded by `sp_getapplock @Resource = 'changepoint_scan'`, 90-day scan window. Locked by `test_breach_gaps_and_islands_synthetic` (golden test 3) and `test_changepoint_scan_and_replace_idempotent` (golden test 4).
2. **The detectors are concrete classes, not strategies behind `IAnomalyStrategy`.** Per ADR-0011 + ADR-0017, no interface was extracted — the three classes have naturally-shaped public methods: `scan_recent(snap)` for the two point-in-time detectors, `rescan_window(snap, days)` for the changepoint detector. The nightly orchestrator (slice 8's `AnomalyOrchestrator`) calls each by name. Differing scan windows and idempotency strategies are visible at the call site, not hidden behind a uniform `detect(start, end)` signature. The "three smaller modules or one orchestrator with three strategy classes" line above is superseded by "three concrete modules + one explicit orchestrator."
