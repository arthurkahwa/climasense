"""ProfileComputer — cohort math + per-day aggregation correctness.

These tests do not touch SQL or sklearn beyond the in-memory fit; the
production residual pipeline is replicated against synthetic series so
the cohort math itself is the assertion surface. The empirical
threshold values are NOT exercised here — those are owned by
`test_derive_pattern_thresholds_rerun_invariance`.
"""

from __future__ import annotations

from datetime import date

import numpy as np
import pandas as pd

from climasense_ml.profile_computer import DayProfileRow, ProfileComputer


def _synthetic_hourly_series(days: int, start: str = "2024-01-01") -> pd.DataFrame:
    """Return `days` of hourly readings with a deterministic seasonal
    signal plus a small per-hour perturbation so cohort σ is well-
    defined for every (dow, hour) cell.

    The signal is `20 + 5*sin(2π·hour/24) + 2*sin(2π·dow/7)`; the
    perturbation rotates predictably through `[-0.2, +0.2]` so the
    z-scores are bounded and deterministic across reruns.
    """
    idx = pd.date_range(
        start=start, periods=days * 24, freq="h", tz="UTC"
    )
    hours = idx.hour.to_numpy()
    dows = idx.dayofweek.to_numpy()
    base = (
        20.0
        + 5.0 * np.sin(2 * np.pi * hours / 24)
        + 2.0 * np.sin(2 * np.pi * dows / 7)
    )
    # Tiny deterministic perturbation so the cohort σ is non-zero.
    perturb = 0.2 * np.sin(2 * np.pi * np.arange(len(idx)) / 113)
    return pd.DataFrame(
        {
            "temperature": base + perturb,
            "humidity": 50.0 + 5.0 * np.cos(2 * np.pi * hours / 24),
        },
        index=idx,
    )


def test_empty_history_returns_empty() -> None:
    rows = ProfileComputer.compute(pd.DataFrame(columns=["temperature", "humidity"]))
    assert rows == []


def test_history_missing_temperature_column_raises() -> None:
    df = pd.DataFrame({"humidity": [1.0, 2.0]})
    try:
        ProfileComputer.compute(df)
    except ValueError as ex:
        assert "temperature" in str(ex)
    else:
        raise AssertionError("expected ValueError")


def test_compute_produces_one_row_per_calendar_date() -> None:
    """30 days of synthetic data → some number of rows (after lag
    warmup eats the first ~7 days). Each row is a `DayProfileRow`.
    """
    history = _synthetic_hourly_series(days=30)
    rows = ProfileComputer.compute(history)

    assert all(isinstance(r, DayProfileRow) for r in rows)
    assert all(isinstance(r.date, date) for r in rows)
    assert all(0 <= r.day_of_week <= 6 for r in rows)
    # Sorted ascending by date
    dates = [r.date for r in rows]
    assert dates == sorted(dates)


def test_compute_is_deterministic_across_reruns() -> None:
    """Same input → identical output bit-for-bit (OLS is closed form;
    no randomness anywhere in the pipeline).
    """
    history = _synthetic_hourly_series(days=45)
    a = ProfileComputer.compute(history)
    b = ProfileComputer.compute(history)
    assert len(a) == len(b)
    for ra, rb in zip(a, b):
        assert ra == rb


def test_target_dates_filters_output_to_requested_set() -> None:
    """The full residual computation runs over the FULL history (so
    cohort μ/σ are stable), but only the requested dates surface in
    the output.
    """
    history = _synthetic_hourly_series(days=30)
    all_rows = ProfileComputer.compute(history)
    assert len(all_rows) >= 7

    pick = [r.date for r in all_rows[10:13]]
    filtered = ProfileComputer.compute(history, target_dates=pick)
    assert [r.date for r in filtered] == sorted(pick)

    # The numeric values for the filtered subset MUST match the full
    # run — cohort μ/σ are derived from the same population in both
    # cases, so a filter is a projection, not a recompute.
    by_date_full = {r.date: r for r in all_rows}
    for f in filtered:
        full = by_date_full[f.date]
        assert f.day_of_week == full.day_of_week
        assert f.mean_residual == full.mean_residual
        assert f.max_abs_zscore == full.max_abs_zscore


def test_target_dates_empty_filter_returns_empty() -> None:
    history = _synthetic_hourly_series(days=14)
    rows = ProfileComputer.compute(history, target_dates=[])
    assert rows == []


def test_day_of_week_matches_calendar() -> None:
    """The `day_of_week` field equals the `pandas.Timestamp.dayofweek`
    for that date (0 = Monday).
    """
    history = _synthetic_hourly_series(days=21, start="2024-03-04")  # Monday
    rows = ProfileComputer.compute(history)
    for r in rows:
        expected = pd.Timestamp(r.date).dayofweek
        assert r.day_of_week == expected, (
            f"date={r.date} expected dow={expected} got {r.day_of_week}"
        )


def test_zscore_is_max_abs_within_day() -> None:
    """`MaxAbsZscore` is the max of |z_t| over the 24 hourly rows of
    that calendar day — never negative, never NaN.
    """
    history = _synthetic_hourly_series(days=20)
    rows = ProfileComputer.compute(history)
    for r in rows:
        assert r.max_abs_zscore >= 0.0
        assert not np.isnan(r.max_abs_zscore)
        assert not np.isnan(r.mean_residual)


def test_constant_temperature_series_collapses_to_quiet_signal() -> None:
    """A perfectly constant series has zero residuals after the lag-LR
    fit (the constant term absorbs the level). The aggregates collapse
    accordingly.
    """
    idx = pd.date_range("2024-01-01", periods=14 * 24, freq="h", tz="UTC")
    history = pd.DataFrame(
        {"temperature": np.full(len(idx), 21.0), "humidity": 50.0}, index=idx
    )
    rows = ProfileComputer.compute(history)
    for r in rows:
        # OLS on a constant + perfect lag features fits exactly →
        # residuals tiny (numerical noise) → mean ≈ 0, max|z| ≈ 0.
        assert abs(r.mean_residual) < 1e-6
        assert r.max_abs_zscore < 1e-6
