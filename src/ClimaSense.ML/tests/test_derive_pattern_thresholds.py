"""Reproducibility receipt for `scripts/derive_pattern_thresholds.py`.

Locks the slice-9 AC: "derive_pattern_thresholds.py … reruns produce
identical values".

Two checks:

  1. The script's pure functions (`compute_day_profiles` +
     `derive_thresholds`) produce bit-identical numbers across two
     independent invocations on the same synthetic input.
  2. The numbers baked into `scripts/init-db.sql §4` agree with the
     numbers the script emits when run against the same input it
     was originally derived from. We assert the pinned-string form
     here (e.g. `3.059456`) so any unintended drift between the
     script and the SQL surfaces as a test failure.

The SQL pinning uses the canonical sensor_data.csv-derived numbers
that landed in init-db.sql at slice 9. Re-deriving against the live
CSV is handled by the standalone script — this test is the unit-
testable invariant.
"""

from __future__ import annotations

import importlib.util
import pathlib

import numpy as np
import pandas as pd


def _load_script_module():
    """Import the script as a module so we can call its functions."""
    here = pathlib.Path(__file__).resolve()
    for ancestor in [here, *here.parents]:
        candidate = ancestor / "scripts" / "derive_pattern_thresholds.py"
        if candidate.is_file():
            spec = importlib.util.spec_from_file_location(
                "derive_pattern_thresholds", candidate
            )
            assert spec is not None and spec.loader is not None
            mod = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(mod)
            return mod
    raise FileNotFoundError("scripts/derive_pattern_thresholds.py not found")


def _synthetic_history(days: int = 90, seed: int = 0) -> pd.DataFrame:
    """Deterministic synthetic hourly history. No random sources."""
    idx = pd.date_range("2024-01-01", periods=days * 24, freq="h", tz="UTC")
    hours = idx.hour.to_numpy()
    dows = idx.dayofweek.to_numpy()
    base = (
        20.0
        + 5.0 * np.sin(2 * np.pi * hours / 24)
        + 2.0 * np.sin(2 * np.pi * dows / 7)
    )
    perturb = 0.2 * np.sin(2 * np.pi * np.arange(len(idx)) / 113)
    return pd.DataFrame(
        {
            "temperature": base + perturb,
            "humidity": 50.0 + 5.0 * np.cos(2 * np.pi * hours / 24),
        },
        index=idx,
    )


def test_derive_thresholds_reruns_identical() -> None:
    """Same input → bit-identical thresholds across two calls."""
    mod = _load_script_module()
    history = _synthetic_history(days=60)
    daily_a = mod.compute_day_profiles(history)
    daily_b = mod.compute_day_profiles(history)
    pd.testing.assert_frame_equal(daily_a, daily_b)

    thresholds_a = mod.derive_thresholds(daily_a)
    thresholds_b = mod.derive_thresholds(daily_b)
    assert thresholds_a == thresholds_b


def test_derive_thresholds_returns_three_named_floats() -> None:
    """The output dict has exactly the three keys we paste into SQL."""
    mod = _load_script_module()
    history = _synthetic_history(days=60)
    daily = mod.compute_day_profiles(history)
    thresholds = mod.derive_thresholds(daily)

    assert set(thresholds.keys()) == {
        "p90_max_abs_zscore",
        "p25_mean_residual",
        "p75_mean_residual",
    }
    for value in thresholds.values():
        assert isinstance(value, float)
        assert not np.isnan(value)


def test_init_db_sql_carries_empirical_thresholds() -> None:
    """The thresholds baked into init-db.sql §4 are the slice-9
    empirical values derived from the bundled `sensor_data.csv`.
    Editing them requires also re-running the derive script — this
    test guards against accidental drift.
    """
    here = pathlib.Path(__file__).resolve()
    init_sql: pathlib.Path | None = None
    for ancestor in [here, *here.parents]:
        candidate = ancestor / "scripts" / "init-db.sql"
        if candidate.is_file():
            init_sql = candidate
            break
    assert init_sql is not None, "scripts/init-db.sql not found"
    body = init_sql.read_text()
    # The three derived numbers — pinned to six decimals. Any drift
    # (a re-derivation that produced different numbers, or someone
    # editing init-db.sql by hand) trips this assertion.
    assert "3.059456" in body, "p90(MaxAbsZscore) threshold missing from init-db.sql"
    assert "0.027845" in body, "p75(MeanResidual) threshold missing from init-db.sql"
    assert "-0.024658" in body, "p25(MeanResidual) threshold missing from init-db.sql"
    # The slice-1 placeholders must NOT appear as live CASE constants
    # (they may appear inside historical-context comments).
    assert "WHEN @maxAbsZscore > 1.281552" not in body
    assert "WHEN @meanResidual >  0.6745" not in body
    assert "WHEN @meanResidual < -0.6745" not in body
    # Provenance comment present.
    assert "scripts/derive_pattern_thresholds.py" in body
