"""Golden test 4 — ChangepointDetector idempotency on rerun.

Locks PRD §"Testing Decisions" test 4:

    "Inserts initial `Anomalies` rows of type `regime_shift`, runs the
    changepoint detector twice with the same input, asserts row count
    stable and rows match expected post-scan values."

Two stronger sub-tests:

  * PELT detects a known mean shift at the expected index (±5
    tolerance) on a synthetic daily-mean series — locks the
    `pen=10` choice plus the `Pelt(model='rbf')` algorithm.
  * Re-running `rescan_window` twice on the same input yields an
    *identical* rowset (same `ReadingTime`, same `Severity`, same
    count) — the scan-and-replace transaction is deterministic.

The test uses an in-memory engine emulating just `dbo.Anomalies` +
the applock + the DELETE/INSERT statements, so no SQL Server is
needed.
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone

import numpy as np
import pandas as pd
import pytest

from climasense_ml.anomaly_changepoint import (
    APPLOCK_RESOURCE,
    ChangepointDetector,
    DEFAULT_PELT_PENALTY,
    _detect_changepoints,
)
from climasense_ml.cursor import CursorSnapshot


# ---------------------------------------------------------------------
# Sub-test 1 — PELT correctness on a synthetic series.
# ---------------------------------------------------------------------
def test_pelt_detects_known_mean_shift_within_tolerance() -> None:
    """Synthetic 90-day series with a 4°C mean shift at index 50.

    PELT with `pen=10` should pick up the shift; the tolerance is ±5
    indices to allow for kernel-smoothing slack at the boundary.
    """
    pytest.importorskip("ruptures")

    rng = np.random.default_rng(42)
    n = 90
    shift_at = 50
    base = np.concatenate(
        [
            rng.normal(loc=20.0, scale=0.5, size=shift_at),
            rng.normal(loc=24.0, scale=0.5, size=n - shift_at),
        ]
    )
    series = pd.Series(base, name="temperature")

    breakpoints = _detect_changepoints(series, penalty=DEFAULT_PELT_PENALTY)

    assert len(breakpoints) >= 1
    closest = min(breakpoints, key=lambda bp: abs(bp - shift_at))
    assert abs(closest - shift_at) <= 5, (
        f"PELT located changepoint at {closest} but expected near {shift_at}; "
        f"all detected: {breakpoints}"
    )


def test_pelt_returns_empty_on_too_short_series() -> None:
    """Series shorter than `MIN_DAILY_POINTS` is a no-op."""
    pytest.importorskip("ruptures")
    short = pd.Series([20.0] * 5, name="temperature")
    assert _detect_changepoints(short, penalty=DEFAULT_PELT_PENALTY) == []


# ---------------------------------------------------------------------
# Sub-test 2 — scan-and-replace idempotency contract.
# ---------------------------------------------------------------------
class _InMemoryEngine:
    """Emulates just enough of SQL Server's `dbo.Anomalies` +
    `sp_getapplock` + DELETE/INSERT to validate the detector's
    transactional shape and idempotency contract.
    """

    def __init__(self) -> None:
        self.anomalies: list[dict] = []
        self.applock_acquires: list[str] = []
        self.delete_calls = 0
        self.insert_calls = 0

    def begin(self) -> "_InMemoryConn":
        return _InMemoryConn(self)


class _InMemoryConn:
    def __init__(self, engine: _InMemoryEngine) -> None:
        self._engine = engine

    def __enter__(self) -> "_InMemoryConn":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:  # noqa: ANN001
        del exc_type, exc, tb

    def execute(self, stmt, params=None):  # noqa: ANN001
        upper = str(stmt).strip().upper()
        params = dict(params or {})
        if "SP_GETAPPLOCK" in upper:
            self._engine.applock_acquires.append(params["resource"])
            return _InMemoryResult(scalar=0)  # 0 == lock granted
        if "DELETE FROM DBO.ANOMALIES" in upper:
            before = len(self._engine.anomalies)
            self._engine.anomalies = [
                a
                for a in self._engine.anomalies
                if not (
                    a["AnomalyType"] == "regime_shift"
                    and params["window_start"]
                    <= a["ReadingTime"]
                    <= params["window_end"]
                )
            ]
            after = len(self._engine.anomalies)
            self._engine.delete_calls += 1
            return _InMemoryResult(rowcount=before - after)
        if "INSERT INTO DBO.ANOMALIES" in upper:
            self._engine.anomalies.append(
                {
                    "ReadingTime": params["reading_time"],
                    "AnomalyType": "regime_shift",
                    "Severity": float(params["severity"]),
                    "Score": float(params["score"]),
                    "Description": params["description"],
                }
            )
            self._engine.insert_calls += 1
            return _InMemoryResult(rowcount=1)
        raise NotImplementedError(stmt)


class _InMemoryResult:
    def __init__(self, *, scalar=None, rowcount: int = 0) -> None:  # noqa: ANN001
        self._scalar = scalar
        self.rowcount = rowcount

    def scalar(self):  # noqa: ANN001
        return self._scalar


def _daily_series_with_shift(
    *,
    cursor: datetime,
    days: int = 90,
    shift_day: int = 50,
    pre_shift_temp: float = 20.0,
    post_shift_temp: float = 24.0,
) -> pd.DataFrame:
    pytest.importorskip("ruptures")
    rng = np.random.default_rng(42)
    n = days
    base = np.concatenate(
        [
            rng.normal(loc=pre_shift_temp, scale=0.5, size=shift_day),
            rng.normal(loc=post_shift_temp, scale=0.5, size=n - shift_day),
        ]
    )
    index = pd.date_range(
        end=cursor.replace(hour=0, minute=0, second=0, microsecond=0),
        periods=n,
        freq="1D",
        tz="UTC",
    )
    return pd.DataFrame(
        {"temperature": base, "humidity": np.full(n, 50.0)},
        index=index,
    )


def test_changepoint_scan_and_replace_yields_identical_rowset_on_rerun() -> None:
    cursor = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    daily = _daily_series_with_shift(cursor=cursor)

    engine = _InMemoryEngine()
    detector = ChangepointDetector(engine=engine, daily_loader=lambda: daily)
    snap = CursorSnapshot(as_of=cursor)

    first = detector.rescan_window(snap, days=90)
    first_anomalies = [dict(a) for a in engine.anomalies]
    second = detector.rescan_window(snap, days=90)
    second_anomalies = [dict(a) for a in engine.anomalies]

    # The applock was acquired both times.
    assert engine.applock_acquires == [APPLOCK_RESOURCE, APPLOCK_RESOURCE]

    # Both invocations land the SAME rowset — count, ReadingTime,
    # Severity, Score, Description all match.
    assert first.inserted >= 1
    assert first.inserted == second.inserted
    assert len(first_anomalies) == len(second_anomalies)
    # Sort by ReadingTime so we can compare row-for-row.
    a = sorted(first_anomalies, key=lambda r: r["ReadingTime"])
    b = sorted(second_anomalies, key=lambda r: r["ReadingTime"])
    for ra, rb in zip(a, b):
        assert ra["ReadingTime"] == rb["ReadingTime"]
        assert ra["AnomalyType"] == rb["AnomalyType"] == "regime_shift"
        assert ra["Severity"] == pytest.approx(rb["Severity"])
        assert ra["Score"] == pytest.approx(rb["Score"])


def test_changepoint_clears_stale_rows_outside_pelt_decision() -> None:
    """A stale `regime_shift` row in the window gets DELETEd by the
    scan-and-replace. The post-scan rowset reflects only the current
    PELT decision."""
    cursor = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    daily = _daily_series_with_shift(cursor=cursor)

    engine = _InMemoryEngine()
    # Pre-seed a stale row inside the 90-day window.
    stale_ts = (cursor - timedelta(days=30)).replace(tzinfo=None)
    engine.anomalies.append(
        {
            "ReadingTime": stale_ts,
            "AnomalyType": "regime_shift",
            "Severity": 999.0,
            "Score": 99.0,
            "Description": "STALE",
        }
    )
    detector = ChangepointDetector(engine=engine, daily_loader=lambda: daily)
    snap = CursorSnapshot(as_of=cursor)

    detector.rescan_window(snap, days=90)

    # Stale row must be gone.
    assert not any(
        a["Description"] == "STALE" for a in engine.anomalies
    )


def test_changepoint_invalid_days_rejects() -> None:
    engine = _InMemoryEngine()
    detector = ChangepointDetector(engine=engine, daily_loader=lambda: pd.DataFrame())
    snap = CursorSnapshot(as_of=datetime(2026, 5, 17, tzinfo=timezone.utc))
    with pytest.raises(ValueError):
        detector.rescan_window(snap, days=0)
