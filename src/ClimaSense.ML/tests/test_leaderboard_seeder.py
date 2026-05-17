"""Tests for `LeaderboardSeeder` — slice 6.

Two test surfaces:

1.  The parser (`load_notebook_entries`) — fed the *real*
    `assets/results.json` from the repo and asserted to produce all
    notebook rows with sensible values. The 5 leaderboard models the
    issue spec names (ARIMA, SARIMA, Holt-Winters, LSTM, 1D-CNN) are
    confirmed present.

2.  The MERGE engine (`LeaderboardSeeder.run`) — exercised against an
    in-memory SQLite engine that mirrors enough of `dbo.Leaderboard`
    for the idempotency check to hold. The MERGE statement targets
    SQL Server syntax, so we feed it a fake engine whose `begin()` /
    `execute()` capture the parameters and assert idempotency by
    counting parameter dicts across two `run()` calls.

The Python-side seeder test cannot run a true SQL Server MERGE without
docker; the .NET-side `LeaderboardEndpointTests` + the live
compose-up verification cover the wire-shape end-to-end.
"""

from __future__ import annotations

import json
import pathlib
from dataclasses import dataclass

import pytest


def _repo_root() -> pathlib.Path:
    here = pathlib.Path(__file__).resolve()
    for parent in [here, *here.parents]:
        if (parent / "contracts" / "openapi.yaml").is_file():
            return parent
    return here.parents[3]


@pytest.fixture(scope="module")
def results_path() -> pathlib.Path:
    return _repo_root() / "assets" / "results.json"


# ---------------------------------------------------------------------
# Parser tests
# ---------------------------------------------------------------------
def test_parser_yields_all_notebook_rows(results_path: pathlib.Path) -> None:
    """`load_notebook_entries` returns at least the 5 named models +
    the lag-LR row (the live row is computed separately).
    """
    from climasense_ml.leaderboard import load_notebook_entries

    if not results_path.is_file():
        pytest.skip(f"results.json missing at {results_path}")

    entries = load_notebook_entries(results_path)
    names = {e.model_name for e in entries}

    # The 5 named models from the issue spec + the lag-LR row whose
    # accuracy is what the live row will be compared against.
    expected_substrings = [
        "Naive (last value)",          # baseline
        "Seasonal naive (lag-24h)",    # baseline
        "Holt-Winters",                # AC mention
        "ARIMA",                       # AC mention
        "SARIMA",                      # AC mention
        "Linear regression (lags)",    # lag-LR
        "LSTM",                        # AC mention
        "Conv1D",                      # AC mention (1D-CNN)
    ]
    for needle in expected_substrings:
        assert any(needle in n for n in names), (
            f"Expected leaderboard model containing {needle!r}; got {names}"
        )

    # Every parsed row has positive metrics — the regex caught only
    # rows that match the two-or-four-float trailing pattern.
    for e in entries:
        assert e.mae > 0
        assert e.rmse > 0
        assert e.provenance == "notebook"


def test_parser_distinguishes_forecast_vs_sequence_blocks(
    results_path: pathlib.Path,
) -> None:
    """`forecast_results` rows carry MAPE / sMAPE; `sequence_results`
    rows do not. Locks the schema's NULL-vs-NOT NULL contract for the
    two MERGE inputs.
    """
    from climasense_ml.leaderboard import load_notebook_entries

    if not results_path.is_file():
        pytest.skip(f"results.json missing at {results_path}")

    entries = load_notebook_entries(results_path)
    # Find at least one row from each block.
    forecast_rows = [e for e in entries if e.mape is not None]
    sequence_rows = [e for e in entries if e.mape is None]
    assert forecast_rows, "expected at least one forecast_results row with mape"
    assert sequence_rows, "expected at least one sequence_results row without mape"


def test_parser_picks_up_lag_lr_numerics_within_1e_3(
    results_path: pathlib.Path,
) -> None:
    """The lag-LR row's MAE / RMSE in `assets/results.json` are
    0.214410 / 0.293336. The parser must read them faithfully so the
    live row produced by `LagLinearForecaster` is genuinely
    "apples-to-apples" with the notebook's lag-LR row.
    """
    from climasense_ml.leaderboard import load_notebook_entries

    if not results_path.is_file():
        pytest.skip(f"results.json missing at {results_path}")

    entries = load_notebook_entries(results_path)
    lag_lr = next(
        (e for e in entries if "Linear regression" in e.model_name), None
    )
    assert lag_lr is not None, "lag-LR row missing from parsed entries"
    assert abs(lag_lr.mae - 0.214410) < 1e-3
    assert abs(lag_lr.rmse - 0.293336) < 1e-3


def test_parser_raises_FileNotFoundError_on_missing(tmp_path: pathlib.Path) -> None:
    from climasense_ml.leaderboard import load_notebook_entries

    missing = tmp_path / "no_such.json"
    with pytest.raises(FileNotFoundError):
        load_notebook_entries(missing)


def test_parser_raises_ValueError_on_empty_results(tmp_path: pathlib.Path) -> None:
    """If `results.json` parses to zero rows the seeder should refuse
    to run — silently MERGE-ing zero rows would mask a notebook-schema
    drift.
    """
    from climasense_ml.leaderboard import load_notebook_entries

    empty = tmp_path / "results.json"
    empty.write_text(json.dumps({"stdouts": {}}))
    with pytest.raises(ValueError):
        load_notebook_entries(empty)


# ---------------------------------------------------------------------
# MERGE-engine tests
# ---------------------------------------------------------------------
#
# We mock the SQLAlchemy engine because the seeder's MERGE statement
# is SQL-Server-specific and a real run requires docker. The mock
# captures one parameter dict per executed statement; idempotency is
# the assertion that running the seeder twice produces the same set
# of parameter dicts both times (i.e. nothing extra is queued, no row
# is dropped).
# ---------------------------------------------------------------------
@dataclass
class _CapturedExec:
    statement_text: str
    params: dict


class _FakeConn:
    def __init__(self, captured: list[_CapturedExec]) -> None:
        self._captured = captured

    def execute(self, stmt, params):  # noqa: ANN001
        self._captured.append(
            _CapturedExec(statement_text=str(stmt), params=dict(params))
        )

        class _Result:
            rowcount = 1

        return _Result()


class _FakeBegin:
    def __init__(self, conn: _FakeConn) -> None:
        self._conn = conn

    def __enter__(self) -> _FakeConn:
        return self._conn

    def __exit__(self, *args) -> None:  # noqa: ANN001
        return None


class _FakeEngine:
    def __init__(self) -> None:
        self.captured: list[_CapturedExec] = []

    def begin(self) -> _FakeBegin:
        return _FakeBegin(_FakeConn(self.captured))


class _FakeFittedForecaster:
    """Stand-in for `LagLinearForecaster` whose `evaluate_on_holdout`
    returns fixed numbers. We do NOT depend on sklearn / pandas for
    the MERGE-engine test.
    """

    fitted = True
    model_version = "lag-lr-v1"

    def evaluate_on_holdout(self, history) -> tuple[float, float]:  # noqa: ANN001
        return (0.214410, 0.293336)


def _build_seeder(engine, results_path: pathlib.Path):  # noqa: ANN001
    from climasense_ml.leaderboard import LeaderboardSeeder

    return LeaderboardSeeder(
        engine=engine,
        results_json_path=results_path,
        forecaster=_FakeFittedForecaster(),
        # Empty list satisfies the "len > 0" guard without needing pandas.
        history_loader=lambda: [1, 2, 3],
    )


def test_seeder_run_merges_notebook_plus_live_row(
    results_path: pathlib.Path,
) -> None:
    if not results_path.is_file():
        pytest.skip(f"results.json missing at {results_path}")

    engine = _FakeEngine()
    seeder = _build_seeder(engine, results_path)
    result = seeder.run()

    assert result.notebook_count >= 5  # we get more than the named 5
    assert result.live_count == 1
    # One MERGE statement per entry.
    assert len(engine.captured) == result.notebook_count + 1

    # Every captured statement is the same MERGE — pin the statement
    # text to catch a refactor that splits the seeder into two SQLs.
    for cap in engine.captured:
        assert "MERGE dbo.Leaderboard" in cap.statement_text
        assert "WHEN NOT MATCHED" in cap.statement_text

    # The last captured statement carries provenance='live' (live row
    # is MERGEd after the notebook rows).
    assert engine.captured[-1].params["provenance"] == "live"
    assert engine.captured[-1].params["model_name"] == "lag-lr-v1"
    # All other rows carry provenance='notebook'.
    for cap in engine.captured[:-1]:
        assert cap.params["provenance"] == "notebook"


def test_seeder_run_is_idempotent_in_parameter_space(
    results_path: pathlib.Path,
) -> None:
    """Two runs produce the same MERGE parameter set (modulo
    `evaluated_at` which is a wall-clock value).

    True row-level idempotency (re-run yields 0 changed rows) is a
    property of the SQL Server MERGE on a real DB; we lock the upstream
    invariant here: the seeder issues exactly the same MERGE
    statements with exactly the same metric values on every run.
    """
    if not results_path.is_file():
        pytest.skip(f"results.json missing at {results_path}")

    engine1 = _FakeEngine()
    _build_seeder(engine1, results_path).run()

    engine2 = _FakeEngine()
    _build_seeder(engine2, results_path).run()

    assert len(engine1.captured) == len(engine2.captured)
    for cap1, cap2 in zip(engine1.captured, engine2.captured, strict=True):
        # Same SQL statement text.
        assert cap1.statement_text == cap2.statement_text
        # Same parameters (excluding the timestamp).
        p1 = {k: v for k, v in cap1.params.items() if k != "evaluated_at"}
        p2 = {k: v for k, v in cap2.params.items() if k != "evaluated_at"}
        assert p1 == p2


def test_seeder_refuses_to_run_with_unfitted_forecaster(
    tmp_path: pathlib.Path, results_path: pathlib.Path
) -> None:
    """Calling `run()` before the boot-fit completes is a programming
    error; the seeder raises `RuntimeError` rather than emitting a
    bogus live row.
    """
    from climasense_ml.leaderboard import LeaderboardSeeder

    if not results_path.is_file():
        pytest.skip(f"results.json missing at {results_path}")

    class _UnfittedForecaster:
        fitted = False
        model_version = "lag-lr-v1"

        def evaluate_on_holdout(self, history):  # noqa: ANN001
            raise AssertionError("should not be called")

    seeder = LeaderboardSeeder(
        engine=_FakeEngine(),
        results_json_path=results_path,
        forecaster=_UnfittedForecaster(),  # type: ignore[arg-type]
        history_loader=lambda: [1, 2, 3],
    )
    with pytest.raises(RuntimeError, match="fitted"):
        seeder.run()


def test_seeder_live_metric_matches_notebook_lag_lr_within_1e_6(
    results_path: pathlib.Path,
) -> None:
    """AC #3 of issue #8: the live row's MAE / RMSE for lag-LR matches
    the notebook's lag-LR row within ε.

    The fake forecaster returns the notebook's exact numbers; the
    seeder must MERGE them verbatim into the live-row payload so the
    on-disk row is bit-identical to the notebook row.

    On a real container this is the place where sklearn-version drift
    would show up — the boot-fit's evaluate_on_holdout produces the
    actual numbers from the live coefficients; this Python test asserts
    the seeder is a pure pipeline that does not lossy-round the
    forecaster's output.
    """
    if not results_path.is_file():
        pytest.skip(f"results.json missing at {results_path}")

    engine = _FakeEngine()
    seeder = _build_seeder(engine, results_path)
    seeder.run()

    live_params = engine.captured[-1].params
    assert live_params["provenance"] == "live"
    assert abs(live_params["mae"] - 0.214410) <= 1e-6
    assert abs(live_params["rmse"] - 0.293336) <= 1e-6
