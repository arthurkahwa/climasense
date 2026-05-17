"""Slice-3 verification tests for `IngestionService` (Python tier).

Locks the architectural claims of slice 3:

  AC1  — `bootstrap_from_csv_if_empty()` is a no-op when SensorReadings
         already has rows (idempotency).
  AC2  — `bootstrap_from_csv_if_empty()` runs the pandas transform
         (drop `id`, rename to canonical column names, dedup on
         `ReadingTime`) when SensorReadings is empty.
  AC3  — The transform produces `(ReadingTime, Temperature, Humidity)`
         in that order, with no `id` column, no duplicate timestamps,
         and a header row in the seed CSV.
  AC4  — `IngestionService` raises `BcpUnavailableError` (a clear,
         actionable exception) when `bcp` is missing from PATH.
  AC5  — `pull_increment()` is a stub that raises `NotImplementedError`
         (it lands in a future WallClock-only slice).

The tests use a fake `row_counter` callable for the empty-table probe and
a fake `bcp_runner` callable so we never need a live SQL Server or a
real `bcp` binary to validate the orchestration logic.
"""

from __future__ import annotations

import csv
import pathlib
import textwrap
from dataclasses import dataclass

import pytest


# ---------------------------------------------------------------------
# Test fixture: a tiny CSV laid out exactly like sensor_data.csv.
# ---------------------------------------------------------------------
_CSV_HEADER = "id,sensor_dateTime,temperature,humidity"
_CSV_ROWS = [
    # Two rows for the SAME timestamp — dedup keeps the first only.
    "1,2019-07-09 14:56:20.000,20,36",
    "2,2019-07-09 14:56:20.000,21,37",
    # A normal monotonic row.
    "3,2019-07-10 14:55:25.000,19,36",
    # Another duplicate timestamp — dedup keeps the first only.
    "4,2019-07-10 14:55:25.000,42,99",
    # And a final unique row.
    "5,2019-07-11 09:00:00.000,18,40",
]


def _write_fixture_csv(path: pathlib.Path) -> None:
    """Write the test fixture CSV in the shape that pandas will consume."""
    path.write_text("\n".join([_CSV_HEADER, *_CSV_ROWS]) + "\n")


@pytest.fixture
def fixture_csv(tmp_path: pathlib.Path) -> pathlib.Path:
    csv_path = tmp_path / "sensor_data.csv"
    _write_fixture_csv(csv_path)
    return csv_path


@pytest.fixture
def seed_csv(tmp_path: pathlib.Path) -> pathlib.Path:
    return tmp_path / "seed.csv"


# ---------------------------------------------------------------------
# Test doubles for the row-count probe and the bcp invocation.
# ---------------------------------------------------------------------
@dataclass
class _BcpInvocation:
    """Capture the (argv, env) of one bcp call so the test can assert on it."""

    argv: list[str]
    env: dict[str, str]


class _FakeBcp:
    """Stand-in for `subprocess.run` that records the invocation and
    returns a successful CompletedProcess-shaped object."""

    def __init__(self) -> None:
        self.calls: list[_BcpInvocation] = []
        self.returncode = 0

    def __call__(self, argv: list[str], *, env: dict[str, str], **kwargs) -> object:
        self.calls.append(_BcpInvocation(argv=list(argv), env=dict(env)))

        class _Result:
            returncode = self.returncode
            stdout = "rows copied 4\n"
            stderr = ""

        return _Result()


# ---------------------------------------------------------------------
# Import target — keep below the fixtures so a missing module is a
# loud failure rather than a collection-time error.
# ---------------------------------------------------------------------
from climasense_ml.ingestion import (  # noqa: E402
    BcpUnavailableError,
    IngestionService,
    transform_to_seed_csv,
)


# ---------------------------------------------------------------------
# AC2/AC3: pandas transform.
# ---------------------------------------------------------------------
def test_transform_drops_id_and_renames_columns(
    fixture_csv: pathlib.Path, seed_csv: pathlib.Path
) -> None:
    stats = transform_to_seed_csv(source=fixture_csv, destination=seed_csv)

    assert seed_csv.exists()
    with seed_csv.open() as fh:
        reader = csv.reader(fh)
        header = next(reader)
        rows = list(reader)

    assert header == ["ReadingTime", "Temperature", "Humidity"]
    # Five raw rows minus two duplicates = three unique timestamps.
    assert len(rows) == 3
    assert stats.raw_rows == 5
    assert stats.deduped_rows == 3
    # The first occurrence wins for each duplicate. Timestamps are kept
    # as raw strings — pandas preserves the upstream ".000" millisecond
    # suffix, which bcp parses into DATETIME2(3) without re-formatting.
    by_time = {row[0]: row for row in rows}
    assert by_time["2019-07-09 14:56:20.000"][1] == "20.0"
    assert by_time["2019-07-09 14:56:20.000"][2] == "36.0"
    assert by_time["2019-07-10 14:55:25.000"][1] == "19.0"
    assert by_time["2019-07-10 14:55:25.000"][2] == "36.0"
    assert by_time["2019-07-11 09:00:00.000"][1] == "18.0"
    assert by_time["2019-07-11 09:00:00.000"][2] == "40.0"


def test_transform_seed_csv_has_no_pandas_index_column(
    fixture_csv: pathlib.Path, seed_csv: pathlib.Path
) -> None:
    """pandas' default `to_csv(index=True)` would prepend a numeric
    column; bcp would then fail with a column-count mismatch. Guard
    against that regression by asserting the header has exactly three
    columns in the expected order."""

    transform_to_seed_csv(source=fixture_csv, destination=seed_csv)
    with seed_csv.open() as fh:
        first_line = fh.readline().rstrip("\n")
    assert first_line == "ReadingTime,Temperature,Humidity"


def test_transform_handles_real_world_csv_shape(
    tmp_path: pathlib.Path, seed_csv: pathlib.Path
) -> None:
    """Mirror the exact header found in the bundled sensor_data.csv —
    `id,sensor_dateTime,temperature,humidity`. Any drift on the upstream
    column names is caught here, not at 2.45M-row bcp time."""

    csv_path = tmp_path / "sensor_data.csv"
    csv_path.write_text(
        textwrap.dedent(
            """\
            id,sensor_dateTime,temperature,humidity
            1207613,2019-07-09 14:56:20.000,20,36
            1207953,2019-07-10 14:55:25.000,19,36
            """
        )
    )

    stats = transform_to_seed_csv(source=csv_path, destination=seed_csv)
    assert stats.raw_rows == 2
    assert stats.deduped_rows == 2


# ---------------------------------------------------------------------
# AC1: idempotency.
# ---------------------------------------------------------------------
def test_bootstrap_is_noop_when_table_already_has_rows(
    fixture_csv: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    """When the row-count probe returns >0, no transform happens and no
    bcp invocation fires. The method returns a result tagged 'skipped'."""

    seed = tmp_path / "seed.csv"
    bcp = _FakeBcp()
    svc = IngestionService(
        csv_path=fixture_csv,
        seed_csv_path=seed,
        row_counter=lambda: 12345,
        bcp_runner=bcp,
        bcp_settings={
            "server": "db,1433",
            "user": "sa",
            "password": "secret",
            "database": "ClimaSense",
        },
    )

    result = svc.bootstrap_from_csv_if_empty()

    assert result.skipped is True
    assert result.row_count_at_start == 12345
    assert result.bcp_invoked is False
    assert not seed.exists(), "should not write a seed file when skipping"
    assert bcp.calls == [], "should not invoke bcp when skipping"


def test_bootstrap_runs_transform_and_bcp_when_table_empty(
    fixture_csv: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    seed = tmp_path / "seed.csv"
    bcp = _FakeBcp()
    svc = IngestionService(
        csv_path=fixture_csv,
        seed_csv_path=seed,
        row_counter=lambda: 0,
        bcp_runner=bcp,
        bcp_settings={
            "server": "db,1433",
            "user": "sa",
            "password": "ClimaSense!Dev2026",
            "database": "ClimaSense",
        },
    )

    result = svc.bootstrap_from_csv_if_empty()

    assert result.skipped is False
    assert result.row_count_at_start == 0
    assert result.bcp_invoked is True
    assert result.raw_rows == 5
    assert result.deduped_rows == 3
    assert seed.exists()
    assert len(bcp.calls) == 1


def test_bcp_invocation_matches_expected_argv_shape(
    fixture_csv: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    """Pin the exact bcp argv. Any drift on the flags (format file,
    skip-rows, table-lock hint, batch size) is a regression — this test
    is the smoke alarm.

    Slice-3 contract uses a format file (`-f`) rather than `-c`/`-t`
    because `SensorReadings.Id` is IDENTITY and the seed CSV omits it.
    The format file maps three CSV columns to SQL columns 2/3/4.
    """

    seed = tmp_path / "seed.csv"
    bcp = _FakeBcp()
    svc = IngestionService(
        csv_path=fixture_csv,
        seed_csv_path=seed,
        row_counter=lambda: 0,
        bcp_runner=bcp,
        bcp_settings={
            "server": "db,1433",
            "user": "sa",
            "password": "secret",
            "database": "ClimaSense",
        },
    )

    svc.bootstrap_from_csv_if_empty()

    assert len(bcp.calls) == 1
    argv = bcp.calls[0].argv
    # bcp <table> in <file> ... (the order matters for bcp).
    assert argv[0].endswith("bcp")
    assert argv[1] == "dbo.SensorReadings"
    assert argv[2] == "in"
    assert argv[3] == str(seed)
    # Required flags.
    assert "-S" in argv and argv[argv.index("-S") + 1] == "db,1433"
    assert "-U" in argv and argv[argv.index("-U") + 1] == "sa"
    # Password is supplied via -P (test value, not real secret).
    assert "-P" in argv and argv[argv.index("-P") + 1] == "secret"
    assert "-d" in argv and argv[argv.index("-d") + 1] == "ClimaSense"
    # Format file owns column-mapping (handles IDENTITY column skip).
    assert "-f" in argv
    fmt_arg = argv[argv.index("-f") + 1]
    assert fmt_arg.endswith(".fmt")
    assert pathlib.Path(fmt_arg).exists(), "format file should be written before bcp runs"
    fmt = pathlib.Path(fmt_arg).read_text()
    # Sanity check on the format file's column mapping.
    assert "ReadingTime" in fmt
    assert "Temperature" in fmt
    assert "Humidity" in fmt
    # Without a format file we'd need -c and -t; with -f neither is set.
    assert "-c" not in argv
    assert "-t" not in argv
    assert "-F" in argv and argv[argv.index("-F") + 1] == "2"  # skip header row
    assert "-b" in argv  # batch size set
    # TABLOCK hint, captured via -h, makes the bulk load orders-of-magnitude faster.
    assert "-h" in argv and "TABLOCK" in argv[argv.index("-h") + 1]
    # Error capture flag — non-fatal parse errors land in a side-file.
    assert "-e" in argv


# ---------------------------------------------------------------------
# AC4: bcp missing.
# ---------------------------------------------------------------------
def test_bootstrap_raises_clean_error_when_bcp_missing(
    fixture_csv: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    """When the runner raises `FileNotFoundError` (canonical exception
    for "binary not on PATH"), the service wraps it in our named
    `BcpUnavailableError` with an actionable message."""

    seed = tmp_path / "seed.csv"

    def _missing_bcp(*_args, **_kwargs) -> object:
        raise FileNotFoundError("bcp")

    svc = IngestionService(
        csv_path=fixture_csv,
        seed_csv_path=seed,
        row_counter=lambda: 0,
        bcp_runner=_missing_bcp,
        bcp_settings={
            "server": "db,1433",
            "user": "sa",
            "password": "secret",
            "database": "ClimaSense",
        },
    )

    with pytest.raises(BcpUnavailableError) as ex:
        svc.bootstrap_from_csv_if_empty()
    assert "bcp" in str(ex.value).lower()


def test_bootstrap_raises_when_bcp_returns_nonzero(
    fixture_csv: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    seed = tmp_path / "seed.csv"
    bcp = _FakeBcp()
    bcp.returncode = 1
    svc = IngestionService(
        csv_path=fixture_csv,
        seed_csv_path=seed,
        row_counter=lambda: 0,
        bcp_runner=bcp,
        bcp_settings={
            "server": "db,1433",
            "user": "sa",
            "password": "secret",
            "database": "ClimaSense",
        },
    )

    with pytest.raises(RuntimeError) as ex:
        svc.bootstrap_from_csv_if_empty()
    assert "bcp" in str(ex.value).lower()


# ---------------------------------------------------------------------
# AC5: pull_increment stub.
# ---------------------------------------------------------------------
def test_pull_increment_is_a_stub_raising_not_implemented(
    fixture_csv: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    """The slice-3 contract: `pull_increment()` exists but is not wired —
    its scheduler registration belongs to a later WallClock-only slice.
    Calling it must raise a clear `NotImplementedError`, not return
    silently (silent stubs lie about the implementation state)."""

    svc = IngestionService(
        csv_path=fixture_csv,
        seed_csv_path=tmp_path / "seed.csv",
        row_counter=lambda: 0,
        bcp_runner=_FakeBcp(),
        bcp_settings={
            "server": "db,1433",
            "user": "sa",
            "password": "secret",
            "database": "ClimaSense",
        },
    )
    with pytest.raises(NotImplementedError):
        svc.pull_increment()


# ---------------------------------------------------------------------
# Sanity: the row counter is invoked exactly once per bootstrap call.
# ---------------------------------------------------------------------
def test_row_counter_is_called_exactly_once(
    fixture_csv: pathlib.Path, tmp_path: pathlib.Path
) -> None:
    seed = tmp_path / "seed.csv"
    calls: list[bool] = []

    def _count() -> int:
        calls.append(True)
        return 0

    svc = IngestionService(
        csv_path=fixture_csv,
        seed_csv_path=seed,
        row_counter=_count,
        bcp_runner=_FakeBcp(),
        bcp_settings={
            "server": "db,1433",
            "user": "sa",
            "password": "secret",
            "database": "ClimaSense",
        },
    )
    svc.bootstrap_from_csv_if_empty()
    assert len(calls) == 1
