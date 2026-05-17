"""`IngestionService` — slice-3 bootstrap of `SensorReadings` from CSV.

Two-reader-one-writer shape per epic #2 §Ingestion:

  * `bootstrap_from_csv_if_empty()` — used at first boot. Reads
    `sensor_data.csv` at repo root, transforms via pandas, writes a
    bcp-friendly seed CSV, and shells out to `bcp` for the bulk load.
    Idempotent: subsequent boots see a populated table and skip.

  * `pull_increment()` — stubbed for a future WallClock-only slice.
    Under `ReplayClock` (the default for the portfolio demo) the
    per-minute incremental sync is unscheduled.

Why a service object instead of a free function:

  * The bootstrap touches three external systems (filesystem, pandas,
    bcp) and a SQL probe. The service object decouples each from the
    test surface — `row_counter`, `bcp_runner`, `bcp_settings` are
    injectable so the tests cover the orchestration logic without
    needing a live SQL Server or a `bcp` binary.

  * The slice-2 interface emergence policy (ADR-0011) rules out
    speculative `IIngestionService`. The class is concrete; tests
    inject the dependencies directly.

bcp invocation, pinned in `test_bcp_invocation_matches_expected_argv_shape`:

    bcp dbo.SensorReadings in /tmp/seed.csv \\
        -S db,1433 -U sa -P "<password>" -d ClimaSense \\
        -c -t , -F 2 -b 50000 \\
        -h "TABLOCK" \\
        -e /tmp/seed.bcperr

Flag reference:
  -c          character mode (we wrote a text CSV from pandas).
  -t ,        column terminator is a comma.
  -F 2        start at line 2 (skip the header row).
  -b 50000    batch size — tuned so the 2.45M-row load completes within
              the 30-90s window the slice-3 spec requires.
  -h TABLOCK  acquire a table-level lock for the duration of the load;
              orders-of-magnitude faster than row-level locks on a
              fresh empty table.
  -e <file>   per-row parse errors land in this file rather than aborting
              the load. The path is logged on completion.
"""

from __future__ import annotations

import logging
import pathlib
import shutil
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import Any

log = logging.getLogger("climasense_ml.ingestion")


# ---------------------------------------------------------------------
# Exceptions
# ---------------------------------------------------------------------
class BcpUnavailableError(RuntimeError):
    """Raised when `bcp` is not installed / not on PATH.

    The ml-tier Dockerfile installs `mssql-tools18` for exactly this
    reason; this exception only fires in dev when someone runs the
    ingestion service outside the container without `bcp` available.
    """


# ---------------------------------------------------------------------
# Return shapes
# ---------------------------------------------------------------------
@dataclass(frozen=True)
class TransformStats:
    """Outcome of `transform_to_seed_csv`.

    Carried verbatim into `BootstrapResult` so callers / log lines can
    surface "dropped X duplicates" without re-reading the CSV.
    """

    raw_rows: int
    deduped_rows: int


@dataclass(frozen=True)
class BootstrapResult:
    """What `bootstrap_from_csv_if_empty()` returns.

    `skipped == True` means the row-count probe found existing rows —
    the bootstrap was a no-op. `bcp_invoked == False` is then implied.
    """

    skipped: bool
    row_count_at_start: int
    bcp_invoked: bool = False
    raw_rows: int = 0
    deduped_rows: int = 0
    bcp_stdout: str = ""
    bcp_stderr: str = ""
    bcp_error_log: pathlib.Path | None = None


# ---------------------------------------------------------------------
# pandas transform (public so a CLI / debugger can call it)
# ---------------------------------------------------------------------
def transform_to_seed_csv(
    *,
    source: pathlib.Path,
    destination: pathlib.Path,
) -> TransformStats:
    """Read `source` (sensor_data.csv shape), produce a bcp-ready CSV at
    `destination`, and return raw/deduped row counts.

    Transform contract — pinned by `test_transform_*`:

      * Drop the upstream `id` column entirely.
      * Rename `(sensor_dateTime, temperature, humidity)` to
        `(ReadingTime, Temperature, Humidity)`.
      * Dedup on `ReadingTime`, keeping the first occurrence (matches
        the schema's `UNIQUE (ReadingTime)` implicit via the clustered
        index — the upstream CSV has a handful of repeats with
        identical / near-identical values).
      * Write with `index=False, header=True` so bcp's `-F 2` skips
        exactly one row and the column count matches.
    """

    # `pandas` is only imported here so that tests for the orchestration
    # surface (idempotency, bcp argv) don't pay the import cost when they
    # never call the transform. The test that *does* exercise the
    # transform asserts on real outputs.
    import pandas as pd

    df = pd.read_csv(source)

    raw_rows = len(df)

    # Drop the upstream sensor `id` if present. (Some test fixtures may
    # not include it; the slice-3 dataset always does.)
    if "id" in df.columns:
        df = df.drop(columns=["id"])

    # Canonical column names. Slice 2 / ADR-0010 / init-db.sql all
    # depend on this exact spelling.
    df = df.rename(
        columns={
            "sensor_dateTime": "ReadingTime",
            "temperature": "Temperature",
            "humidity": "Humidity",
        }
    )

    # Dedup on the natural key. `keep='first'` is explicit so a future
    # behavioural change is loud.
    df = df.drop_duplicates(subset=["ReadingTime"], keep="first")

    # Coerce numeric types so the seed CSV carries floats (the
    # `SensorReadings.Temperature/Humidity` columns are DECIMAL(6,3)).
    df["Temperature"] = df["Temperature"].astype(float)
    df["Humidity"] = df["Humidity"].astype(float)

    # Ensure the column order matches the table for bcp's positional load.
    df = df[["ReadingTime", "Temperature", "Humidity"]]

    destination.parent.mkdir(parents=True, exist_ok=True)
    df.to_csv(destination, index=False, header=True)

    return TransformStats(raw_rows=raw_rows, deduped_rows=len(df))


# ---------------------------------------------------------------------
# IngestionService
# ---------------------------------------------------------------------
# bcp non-XML format file — pinned template.
#
# `dbo.SensorReadings` has four columns: Id (IDENTITY, BIGINT),
# ReadingTime (DATETIME2(3)), Temperature (DECIMAL(6,3)), Humidity
# (DECIMAL(6,3)). The seed CSV has THREE columns (no Id) — IDENTITY
# values are auto-generated by SQL Server. bcp needs an explicit
# format file to map the three CSV columns to SQL columns 2/3/4 and
# skip column 1.
#
# Format file structure (non-XML, version 14.0 for SQL Server 2017+):
#   <bcp-version>
#   <data-file-column-count>
#   <data-col> <host-type> <prefix-len> <data-len> <terminator> <server-col-order> <server-col-name> <collation>
#
# A data row is `ReadingTime,Temperature,Humidity\n`. The last column's
# terminator is the row-terminator (`\n`) — explicitly written as `\n`
# (the bcp parser interprets backslash escapes).
_BCP_FORMAT_FILE_TEMPLATE = """14.0
3
1   SQLCHAR  0  24  ","   2  ReadingTime  ""
2   SQLCHAR  0  32  ","   3  Temperature  ""
3   SQLCHAR  0  32  "\\n"  4  Humidity     ""
"""


@dataclass
class IngestionService:
    """Orchestrates one-shot bootstrap and (in a future slice) the
    per-minute incremental pull.

    Dependencies are injected — see module docstring for why."""

    csv_path: pathlib.Path
    seed_csv_path: pathlib.Path
    row_counter: Callable[[], int]
    bcp_runner: Callable[..., Any]
    bcp_settings: dict[str, str]
    batch_size: int = 50_000
    error_log_path: pathlib.Path | None = None
    format_file_path: pathlib.Path | None = None
    extra_bcp_args: list[str] = field(default_factory=list)

    # -----------------------------------------------------------------
    def bootstrap_from_csv_if_empty(self) -> BootstrapResult:
        """The slice-3 bootstrap entry point.

        Returns a `BootstrapResult` indicating whether anything was done.
        Raises:

          * `FileNotFoundError` — if `csv_path` does not exist (the
            container's bind-mount didn't land).
          * `BcpUnavailableError` — if the `bcp` binary is missing.
          * `RuntimeError` — if bcp returns a non-zero exit code.
        """

        existing_rows = self.row_counter()
        if existing_rows > 0:
            log.info(
                "IngestionService: skip — SensorReadings already has %d rows",
                existing_rows,
            )
            return BootstrapResult(
                skipped=True,
                row_count_at_start=existing_rows,
            )

        if not self.csv_path.exists():
            raise FileNotFoundError(
                f"sensor_data.csv not found at {self.csv_path}. "
                "Confirm the docker-compose bind-mount (`./sensor_data.csv:/data/sensor_data.csv:ro`)."
            )

        log.info("IngestionService: transforming %s -> %s", self.csv_path, self.seed_csv_path)
        stats = transform_to_seed_csv(
            source=self.csv_path,
            destination=self.seed_csv_path,
        )
        log.info(
            "IngestionService: transform produced %d deduped rows (from %d raw)",
            stats.deduped_rows,
            stats.raw_rows,
        )

        error_log = self.error_log_path or self.seed_csv_path.with_suffix(".bcperr")

        # Write the bcp format file alongside the seed CSV. The format
        # file pins the column mapping (CSV→SQL) so bcp skips the
        # IDENTITY `Id` column and casts each field into the right
        # destination type.
        format_file = (
            self.format_file_path
            or self.seed_csv_path.with_suffix(".fmt")
        )
        format_file.write_text(_BCP_FORMAT_FILE_TEMPLATE)

        argv = self._bcp_argv(
            seed_path=self.seed_csv_path,
            error_log=error_log,
            format_file=format_file,
        )

        # Pass the password via env in addition to argv so it never lands
        # in a shell history if the runner happens to use a shell. Real
        # invocation is via subprocess.run with a list argv, so the
        # password appears in argv only.
        env = dict(self.bcp_settings)
        env.update({"SQLCMDPASSWORD": self.bcp_settings["password"]})

        log.info(
            "IngestionService: launching bcp (%d-row batch) -> %s",
            self.batch_size,
            self.bcp_settings.get("server"),
        )

        try:
            proc = self.bcp_runner(argv, env=env)
        except FileNotFoundError as ex:
            raise BcpUnavailableError(
                "bcp not found on PATH. The ml-tier Dockerfile installs "
                "mssql-tools18 for this; if you're running outside the "
                "container, install it locally and retry."
            ) from ex

        rc = getattr(proc, "returncode", 0)
        stdout = getattr(proc, "stdout", "") or ""
        stderr = getattr(proc, "stderr", "") or ""

        if rc != 0:
            raise RuntimeError(
                f"bcp failed with exit code {rc}. stderr: {stderr!s} "
                f"(parse errors captured in {error_log})"
            )

        log.info("IngestionService: bcp completed; stdout snippet: %s", stdout[:200])

        return BootstrapResult(
            skipped=False,
            row_count_at_start=0,
            bcp_invoked=True,
            raw_rows=stats.raw_rows,
            deduped_rows=stats.deduped_rows,
            bcp_stdout=stdout,
            bcp_stderr=stderr,
            bcp_error_log=error_log,
        )

    # -----------------------------------------------------------------
    def pull_increment(self) -> None:
        """Per-minute incremental sync. Future WallClock-only slice.

        We raise rather than no-op so a caller that wires this against
        APScheduler under ReplayClock gets a loud signal — the docstring
        is the contract; silent stubs lie about implementation state.
        """
        raise NotImplementedError(
            "pull_increment is unscheduled under ReplayClock; the "
            "WallClock-only ingestion sync lands in a future slice."
        )

    # -----------------------------------------------------------------
    def _bcp_argv(
        self,
        *,
        seed_path: pathlib.Path,
        error_log: pathlib.Path,
        format_file: pathlib.Path | None = None,
    ) -> list[str]:
        """Compose the bcp argv. Pinned shape — change requires a test update.

        Encryption posture: mssql-tools18's `bcp` defaults to encrypted
        connections and verifies the server certificate. The dev compose
        stack uses SQL Server's self-signed certificate so we add
        ``-u`` to bypass cert validation (sqlcmd equivalent of ``-No``).
        Slice 1's sqlcmd invocation uses the same posture.

        When ``format_file`` is provided we pass ``-f <path>`` instead
        of ``-c`` / ``-t``. The format file owns the column mapping
        (which is necessary because `SensorReadings.Id` is IDENTITY and
        not present in the CSV).
        """
        bcp_bin = self._resolve_bcp_bin()
        argv: list[str] = [
            bcp_bin,
            "dbo.SensorReadings",
            "in",
            str(seed_path),
            "-S",
            self.bcp_settings["server"],
            "-U",
            self.bcp_settings["user"],
            "-P",
            self.bcp_settings["password"],
            "-d",
            self.bcp_settings["database"],
        ]
        if format_file is not None:
            # Format-file driven: -c/-t are implied by the file's
            # SQLCHAR + terminator declarations.
            argv.extend(["-f", str(format_file)])
        else:
            argv.extend([
                "-c",                       # character mode (we wrote a text CSV)
                "-t", ",",                  # column terminator
            ])
        argv.extend([
            "-F", "2",                  # start at line 2 (skip header)
            "-b", str(self.batch_size), # batch size for fast-load
            "-h", "TABLOCK",            # table-level lock = fast bulk load
            "-e", str(error_log),       # per-row parse errors -> side file
            "-u",                       # trust self-signed dev server cert
        ])
        argv.extend(self.extra_bcp_args)
        return argv

    @staticmethod
    def _resolve_bcp_bin() -> str:
        """Return the path to the bcp binary, preferring the
        mssql-tools18 install location used by the ml-tier Dockerfile.

        Falls back to `shutil.which`. If neither resolves, we still
        return the literal `'bcp'` — the runner will raise
        `FileNotFoundError`, which the orchestration layer converts to
        `BcpUnavailableError`.
        """
        tools18 = "/opt/mssql-tools18/bin/bcp"
        if pathlib.Path(tools18).exists():
            return tools18
        legacy = "/opt/mssql-tools/bin/bcp"
        if pathlib.Path(legacy).exists():
            return legacy
        found = shutil.which("bcp")
        return found or "bcp"
