# 10. Sensor readings sourced from an upstream MS SQL Server

Date: 2026-05-08
Status: Accepted

## Context

The original spec sourced sensor readings from `sensor_data.csv` and a `scripts/import-data.sql` bulk loader. The real production data lives in an upstream MS SQL Server owned by the sensor producer; the CSV in this repo is a one-time export taken for the notebook analysis (`Climate_Time_Series_Analysis.ipynb`). The deployed platform should read from the upstream server, not from the CSV.

This is independent of ADR-0004 (Replay mode): Replay still operates on the data inside ClimaSense's own SQL Server, which is now populated by ingestion rather than CSV import.

## Decision

Add an **`IngestionService`** in the Python (`src/ClimaSense.ML`) tier that reads from the upstream MS SQL Server via SQLAlchemy + pyodbc and writes into ClimaSense's own `SensorReadings` table.

- **Initial load**: idempotent full import on first run. `INSERT … WHERE NOT EXISTS` keyed on `(ReadingTime)`.
- **Incremental sync**: APScheduler job pulls rows where `ReadingTime > MAX(ReadingTime)` every minute.
- **Credentials**: read-only on the upstream server. Connection string injected via env var (`UPSTREAM_DB_CONN`) so neither secrets nor the host live in the repo.
- **CSV stays** in the repo as the notebook's reproducible fixture. The platform never reads from it; only the notebook does.

The upstream schema is assumed to expose `(ReadingTime, Temperature, Humidity)` columns or a view that does.

## Consequences

- `scripts/import-data.sql` is dropped from the Code Structure tree. Replaced by `src/ClimaSense.ML/services/ingestion_service.py`.
- Days 1–2 of the roadmap (ADR-0009) update: foundation work now includes the upstream connection and initial mirror, not a CSV bulk import.
- The Mermaid architecture graph gains a "Source SQL Server (upstream)" node feeding the ML service.
- Tech Stack adds an explicit "Data source" row.
- Failure modes: upstream unavailable → ingestion logs and retries; ClimaSense's own DB continues to serve cached history. The dashboard's "current reading" reflects the latest successfully mirrored row.
- A future deployment that wants live ingestion against wall-clock simply switches `IClock` from `ReplayClock` to `WallClock` (ADR-0004); the ingestion path is the same.
- Out of scope: schema reconciliation between upstream and ClimaSense if the upstream schema differs from the assumed shape. A `IngestionAdapter` interface can absorb that later.

## Amendment — 2026-05-20 (post-slice-13)

Slice 3 + ADR-0013 (bcp bootstrap strategy) reshaped this decision for the portfolio demo:

1. **CSV bootstrap is the demo path; upstream-DB ingestion is the production path.** The bundled `sensor_data.csv` (~116 MB, ~3.07 M raw rows, ~2.45 M deduped) at repo root is the demo's seed. `IngestionService.bootstrap_from_csv_if_empty()` runs on first boot, shells out to `bcp` for the ~30–60 s bulk load, and exits. The "CSV stays as a notebook fixture; platform never reads it" line above is walked back — the platform DOES read it on first boot under `ReplayClock`. The fixture survives because the notebook can re-execute against the same file.
2. **Upstream-view contract.** `UPSTREAM_VIEW_NAME` env var (default `dbo.SensorReadings`) names the single-sensor view the platform expects. Multi-sensor selection is the upstream owner's responsibility — see ADR-0008's single-zone scope. The view exposes `(ReadingTime, Temperature, Humidity)`.
3. **Incremental sync runs under WallClock only.** Per ADR-0004's amendment, `pull_increment()` is registered with APScheduler only when `CLIMASENSE_CLOCK_MODE=wall`. Under `ReplayClock` (the default demo mode) the job is unscheduled; the bootstrap is one-shot and the platform is stable thereafter. The `pull_increment` body is `NotImplementedError` in slice 12 — implementing it is a future slice (out of scope for the 14-day build per ADR-0009's amendment).
4. **`scripts/import-data.sql` was never created.** Per slice 1 + ADR-0013 the bootstrap is `bcp`-driven from Python, not from SQL. The Code Structure tree in the README reflects this.
