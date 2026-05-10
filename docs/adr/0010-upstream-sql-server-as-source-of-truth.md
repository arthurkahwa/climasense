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
