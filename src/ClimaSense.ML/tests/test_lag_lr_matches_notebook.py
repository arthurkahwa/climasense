"""Golden test 1 — boot-fit lag-LR matches the notebook's leaderboard row.

`LagLinearForecaster.fit_at_startup(history)` MUST replicate the
notebook's §8.3 fit and reproduce MAE / RMSE within 1e-6 of the
`assets/results.json::sequence_results` row 0
("Linear regression (lags)") on the same 14-day held-out test window.

The test loads the bundled `sensor_data.csv` at the repo root,
reproduces the notebook's cell-6 + cell-8 transformation pipeline
(sort, dedup, drop id, set index, hourly mean, linear interpolation),
hands the resulting DataFrame to `LagLinearForecaster.fit_at_startup`,
and asserts:

  * MAE matches `results.json::sequence_results[0]` within 1e-6.
  * RMSE matches `results.json::sequence_results[0]` within 1e-6.
  * The fit is deterministic — re-running on the same input yields the
    same numbers (loose `<= 1e-10` tolerance to absorb FP variation).
  * The fit completes in ≤ 5 seconds (the epic's "<1s" target on the
    full 90k-row dataset; CI may be slower so we leave headroom).

The test is SKIPPED when `sensor_data.csv` is not present at the repo
root — the file is 116 MB and gitignored; reviewers fetch it
separately per the README.
"""

from __future__ import annotations

import json
import pathlib
import time

import pytest


def _repo_root() -> pathlib.Path:
    here = pathlib.Path(__file__).resolve()
    for parent in [here, *here.parents]:
        if (parent / "sensor_data.csv").is_file() or (parent / "contracts" / "openapi.yaml").is_file():
            return parent
    return here.parents[3]


def _results_json() -> dict:
    root = _repo_root()
    path = root / "assets" / "results.json"
    if not path.is_file():
        pytest.skip(f"assets/results.json not found at {path}")
    return json.loads(path.read_text())


def _laglr_targets() -> tuple[float, float]:
    """Return `(mae, rmse)` for the `Linear regression (lags)` row.

    `assets/results.json::sequence_results` is rendered as a plain
    `print(df.to_string())` block. The lag-LR row is the first non-
    header data line.
    """
    data = _results_json()
    block = data["stdouts"]["sequence_results"]
    for line in block.splitlines():
        line = line.strip()
        if not line.startswith("0"):
            continue
        # "0       Linear regression (lags)  0.214410  0.293336"
        # Split on whitespace; the last two tokens are MAE / RMSE.
        parts = line.split()
        return float(parts[-2]), float(parts[-1])
    raise AssertionError("Could not find the lag-LR row in results.json")


@pytest.fixture(scope="module")
def hourly_history():
    csv = _repo_root() / "sensor_data.csv"
    if not csv.is_file():
        pytest.skip(
            f"sensor_data.csv not present at {csv}; "
            "fetch the bundled fixture per the README to run the golden test."
        )
    sk = pytest.importorskip(
        "sklearn", reason="scikit-learn must be installed to run the golden test"
    )
    del sk  # only used to gate the import
    from climasense_ml.forecaster import load_hourly_from_csv

    return load_hourly_from_csv(str(csv))


def test_lag_lr_matches_notebook(hourly_history) -> None:
    """The boot-fit reproduces the notebook's lag-LR row exactly.

    Tolerance is 1e-6 absolute on both MAE and RMSE — the spec asks
    for ≤ 1 % relative; 1e-6 absolute is several orders of magnitude
    tighter and catches sklearn-version drift loudly.
    """
    from climasense_ml.forecaster import LagLinearForecaster

    expected_mae, expected_rmse = _laglr_targets()

    forecaster = LagLinearForecaster()
    summary = forecaster.fit_at_startup(hourly_history)

    assert summary.n_train > 80_000, (
        f"Expected ~89k train rows (notebook §8.3); got {summary.n_train}"
    )
    assert summary.n_test == 336, (
        f"Expected 336 test rows (14 days × 24 h); got {summary.n_test}"
    )
    assert summary.n_features == 13, (
        f"Expected 13 features (8 lags + 4 sin/cos + month); got {summary.n_features}"
    )

    assert summary.mae == pytest.approx(expected_mae, abs=1e-6), (
        f"MAE drift: {summary.mae:.6f} vs notebook {expected_mae:.6f}"
    )
    assert summary.rmse == pytest.approx(expected_rmse, abs=1e-6), (
        f"RMSE drift: {summary.rmse:.6f} vs notebook {expected_rmse:.6f}"
    )


def test_boot_fit_is_deterministic(hourly_history) -> None:
    """Re-running the boot-fit on the same input yields identical numbers."""
    from climasense_ml.forecaster import LagLinearForecaster

    f1 = LagLinearForecaster()
    s1 = f1.fit_at_startup(hourly_history)
    f2 = LagLinearForecaster()
    s2 = f2.fit_at_startup(hourly_history)

    assert s1.mae == pytest.approx(s2.mae, abs=1e-12)
    assert s1.rmse == pytest.approx(s2.rmse, abs=1e-12)
    assert s1.n_train == s2.n_train
    assert s1.n_test == s2.n_test


def test_boot_fit_completes_within_budget(hourly_history) -> None:
    """Epic's "<1s" target on 90k rows. CI gets 5s of headroom."""
    from climasense_ml.forecaster import LagLinearForecaster

    forecaster = LagLinearForecaster()
    t0 = time.perf_counter()
    forecaster.fit_at_startup(hourly_history)
    elapsed = time.perf_counter() - t0
    assert elapsed < 5.0, f"Boot-fit took {elapsed:.2f}s; budget is 5s"


def test_predict_shape_and_index(hourly_history) -> None:
    """`predict(history_tail, horizon_hours)` returns a `(horizon, ≥3)` frame
    indexed by UTC `target_time` with monotonically increasing hourly stamps."""
    from datetime import timedelta

    from climasense_ml.forecaster import LAGS, LagLinearForecaster

    forecaster = LagLinearForecaster()
    forecaster.fit_at_startup(hourly_history)

    tail = hourly_history.tail(max(LAGS) + 24)
    horizon = 72
    points = forecaster.predict(tail, horizon_hours=horizon)

    assert len(points) == horizon
    assert {
        "predicted_temperature",
        "predicted_humidity",
        "confidence_lower_temp",
        "confidence_upper_temp",
    }.issubset(points.columns)

    # Index is hourly UTC.
    diffs = points.index.to_series().diff().dropna().unique()
    assert len(diffs) == 1 and diffs[0] == timedelta(hours=1), (
        f"Expected uniform 1h gap; got {diffs}"
    )

    # All predictions are finite numbers.
    import numpy as np

    for col in ("predicted_temperature", "predicted_humidity"):
        assert np.all(np.isfinite(points[col].to_numpy())), (
            f"Column {col} contains non-finite values"
        )

    # CI bands envelope the point estimate.
    assert (points["confidence_lower_temp"] <= points["predicted_temperature"]).all()
    assert (points["confidence_upper_temp"] >= points["predicted_temperature"]).all()


def test_no_iforecaster_protocol_in_codebase() -> None:
    """AC #9: no `IForecaster` Protocol/interface exists anywhere in
    the codebase (`grep -r 'IForecaster' src/` minus prose comments
    and minus this test file returns zero matches).

    This is the structural enforcement of ADR-0011's interface-emergence
    policy: no speculative Protocol seam exists for the forecaster.

    We scan for *load-bearing* references (imports, class declarations,
    type annotations, base-class lists) — prose mentions in docstrings
    or comments don't count as "the seam exists".
    """
    import re

    root = _repo_root()
    src = root / "src"
    if not src.is_dir():
        pytest.skip(f"src/ tree not found at {src}")

    # Patterns that would indicate an actual interface seam (not a
    # prose mention). The first group catches Python — imports,
    # `class IForecaster(`, `: IForecaster`, `IForecaster()`. The second
    # group catches C# — `interface IForecaster`, `class … : IForecaster`,
    # `IForecaster ` (typed declaration), generic positions.
    load_bearing = re.compile(
        r"""(
            (?:from\s+[\w.]+\s+import\s+[^#\n]*\bIForecaster\b)  # py import
          | (?:^\s*import\s+[^#\n]*\bIForecaster\b)              # py bare import
          | (?:\bclass\s+IForecaster\b)                          # py / cs class decl
          | (?:\binterface\s+IForecaster\b)                      # cs interface decl
          | (?::\s*IForecaster\b)                                # cs / py base list / anno
          | (?:->\s*IForecaster\b)                               # py return anno
          | (?:\bIForecaster\b\s*[\[<(])                         # generic / call / index
          | (?:\bIForecaster\b\s+\w)                             # cs typed declaration
        )""",
        re.MULTILINE | re.VERBOSE,
    )

    hits: list[str] = []
    here = pathlib.Path(__file__).resolve()
    for path in src.rglob("*"):
        if not path.is_file():
            continue
        if path.suffix not in {".py", ".cs"}:
            continue
        # Skip the Generated/ subtree (Kiota output).
        if "Generated" in path.parts:
            continue
        # Skip this test file (we mention the identifier in comments).
        if path.resolve() == here:
            continue
        try:
            text = path.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        for line_no, line in enumerate(text.splitlines(), start=1):
            stripped = line.lstrip()
            if stripped.startswith("#") or stripped.startswith("//"):
                continue  # ignore one-line comments
            if load_bearing.search(line):
                hits.append(f"{path}:{line_no}: {line.strip()}")

    assert not hits, (
        "Found load-bearing `IForecaster` references (interface-emergence "
        "policy violation):\n" + "\n".join(hits)
    )
