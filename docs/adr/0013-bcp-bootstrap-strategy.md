# ADR-0013 — bcp Bootstrap Strategy

> Status: accepted (slice 3 / 2026-05-17).
> Supersedes nothing. Refines ADR-0010 (upstream MS SQL Server as the
> source of truth) with the concrete dev-mode bootstrap pipeline.

## Context

ADR-0010 declares that the production source of truth is an upstream
MS SQL Server fronted by a single-sensor view, and that
`IngestionService` mirrors that view into ClimaSense's own
`SensorReadings` table via per-minute incremental sync.

The portfolio demo, however, has no upstream MS SQL Server. The
bundled `sensor_data.csv` (~116 MB, ~3.07 M raw rows) at repo root is
the calibration data the notebook produced — and it doubles as the
demo's seed. Slice 3 needs a one-shot bootstrap path that:

1. Runs on the first `docker compose up` and brings `SensorReadings`
   from empty to ~2.45 M deduped rows in ~30-90 s.
2. Is idempotent — subsequent boots see a populated table and skip.
3. Does not require a separate "bootstrap" step or a sidecar
   container — reviewers run `docker compose up` and nothing else.
4. Is testable without standing up SQL Server (the orchestration
   logic must be exercised in unit tests).

Three loaders were considered:

* **SQLAlchemy `executemany` over pyodbc** — ~10x slower than bcp on
  bulk loads (each row paid the row-level lock overhead). Rejected
  because ~2.45 M rows would take 5-10 minutes, missing the timing
  budget.
* **Pandas `DataFrame.to_sql(method='multi')`** — better than
  `executemany` but still ODBC-bound. Rejected for the same reason.
* **`bcp` from `mssql-tools18`** — purpose-built bulk loader, uses
  `TABLOCK` to acquire a table-level lock for the duration of the
  load. Measured ~28 s for the 2.45 M rows in the slice-3 build.

## Decision

The ml-tier Dockerfile installs `mssql-tools18` (which ships `bcp` at
`/opt/mssql-tools18/bin/bcp`) alongside `msodbcsql18` (already there
for pyodbc). `IngestionService.bootstrap_from_csv_if_empty()`:

1. Probes `SELECT TOP 1 1 FROM dbo.SensorReadings`. If non-zero,
   returns immediately — bootstrap is a no-op.
2. Reads `/data/sensor_data.csv` (bind-mounted from repo root) via
   pandas. Drops the upstream `id` column; renames
   `(sensor_dateTime, temperature, humidity)` →
   `(ReadingTime, Temperature, Humidity)`; dedups on `ReadingTime`
   (keep first); writes to `/tmp/seed.csv` with header + comma
   separator, no index.
3. Writes a non-XML bcp format file at `/tmp/seed.fmt` mapping the
   three CSV columns to SQL columns 2/3/4 (skipping the `Id`
   IDENTITY column).
4. Shells out to:
   ```
   bcp dbo.SensorReadings in /tmp/seed.csv \
       -S db,1433 -U sa -P "$MSSQL_SA_PASSWORD" -d ClimaSense \
       -f /tmp/seed.fmt \
       -F 2 -b 50000 -h "TABLOCK" \
       -e /tmp/seed.bcperr \
       -u
   ```
   Flags: `-f` (format file), `-F 2` (skip header row), `-b 50000`
   (batch size), `-h "TABLOCK"` (table-level lock), `-e` (per-row
   parse errors → side file), `-u` (trust self-signed dev server cert).

The bootstrap runs as a background task during FastAPI's lifespan
startup — the HTTP server accepts traffic immediately, but
`/api/health/ready` returns 503 with `bootstrap=skipped` (the slice-2
`Checks` enum value) until the load completes. The dependent web
service's `depends_on: ml: condition: service_healthy` gates startup
so reviewers never see an empty dashboard.

`pull_increment()` is declared on `IngestionService` but raises
`NotImplementedError`. Its APScheduler registration belongs to a
future WallClock-only slice — under `ReplayClock` (the portfolio
default) the per-minute sync is structurally unscheduled.

## Consequences

**Positive**

* First-boot timing: ~6 s pandas transform + ~28 s bcp = ~34 s on a
  developer laptop, well inside the 30-90 s spec.
* Re-boot timing: <1 s (one-row SQL probe).
* No format-file change required when slice 4 onwards starts writing
  to `SensorReadings` from other code paths — bcp's format file is
  load-time only.
* `mssql-tools18` is a one-time apt install in the ml Dockerfile;
  the binary lives at `/opt/mssql-tools18/bin/bcp` and is unused
  after bootstrap completes.
* Testing: the orchestration logic (idempotency check, pandas
  transform, argv composition) is fully unit-testable via
  injected `row_counter` + `bcp_runner` callables. Tests never need
  a live SQL Server.

**Negative**

* `mssql-tools18` adds ~200 MB to the ml image. The package is only
  needed for bootstrap; alternatives (a sidecar "bootstrap" container
  that exits after loading) would keep the runtime image smaller but
  increase compose complexity and force the readiness gate to depend
  on `service_completed_successfully` of the sidecar.
* The bootstrap is a one-shot — if it fails partway through, the
  table may end up partially populated and the idempotency probe
  (`SELECT TOP 1 1 FROM SensorReadings`) will skip on the next boot.
  Mitigated by bcp's `TABLOCK` semantics (a failed batch is rolled
  back at the batch boundary; the table is empty or fully populated
  for the failed batch).
* The format file pins the CSV→SQL column mapping. Any future schema
  change to `SensorReadings` requires a coordinated update to the
  format file template in `ingestion.py`.

**Future ADRs that this decision invites**

* When the per-minute incremental sync ships, an ADR will pin the
  `MERGE` or `INSERT … WHERE NOT EXISTS` shape for incremental
  ingestion and the cursor / `last-seen ReadingTime` state machine.
* When the upstream is a real MSSQL view (production deployment),
  `bcp` is replaced by an OPENROWSET-style cross-server INSERT. The
  bootstrap pipeline becomes a "WallClock-only init step."
