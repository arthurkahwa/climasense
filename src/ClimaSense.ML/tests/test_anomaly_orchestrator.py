"""Tests for `anomaly_orchestrator.run_all_detectors` + `run_safely`.

Locks the AC: "POST /api/ml/run/anomalies returns the count of newly-
inserted rows by type via `AnomalyRunSummary`."

Three concerns:

  * `run_all_detectors` calls each detector exactly once and aggregates
    counts into `AnomalyRunSummary` (camelCase field names match the
    contract schema).
  * `run_safely` swallows per-detector exceptions and reports `0` for
    that type while letting the other two run.
  * The orchestrator calls the detectors in the documented order
    (sensor_failure → residual_outlier → regime_shift) so log lines
    match.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone

import pytest

from climasense_ml.anomaly_orchestrator import (
    AnomalyRunSummary,
    run_all_detectors,
    run_safely,
)
from climasense_ml.cursor import CursorSnapshot


# ---------------------------------------------------------------------
# Fakes — each detector records the call and returns a configurable
# result. The orchestrator only depends on `scan_recent` /
# `rescan_window` methods so duck-typing is sufficient.
# ---------------------------------------------------------------------
@dataclass
class _FakeScanResult:
    inserted: int
    scanned: int


class _FakeSensor:
    def __init__(self, *, inserted: int = 0, scanned: int = 0, raises=None) -> None:  # noqa: ANN001
        self._result = _FakeScanResult(inserted=inserted, scanned=scanned)
        self._raises = raises
        self.calls = 0

    def scan_recent(self, snap):  # noqa: ANN001
        self.calls += 1
        if self._raises:
            raise self._raises
        return self._result


class _FakeResidual:
    def __init__(self, *, inserted: int = 0, scanned: int = 0, raises=None) -> None:  # noqa: ANN001
        self._result = _FakeScanResult(inserted=inserted, scanned=scanned)
        self._raises = raises
        self.calls = 0

    def scan_recent(self, snap):  # noqa: ANN001
        self.calls += 1
        if self._raises:
            raise self._raises
        return self._result


class _FakeChangepoint:
    def __init__(self, *, inserted: int = 0, scanned: int = 0, raises=None) -> None:  # noqa: ANN001
        self._result = _FakeScanResult(inserted=inserted, scanned=scanned)
        self._raises = raises
        self.calls = 0
        self.last_days: int | None = None

    def rescan_window(self, snap, days):  # noqa: ANN001
        self.calls += 1
        self.last_days = days
        if self._raises:
            raise self._raises
        return self._result


def _snap() -> CursorSnapshot:
    return CursorSnapshot(as_of=datetime(2026, 5, 17, 12, 0, tzinfo=timezone.utc))


# ---------------------------------------------------------------------
# Happy path
# ---------------------------------------------------------------------
def test_run_all_detectors_aggregates_counts_and_calls_each_once() -> None:
    sf = _FakeSensor(inserted=2, scanned=100)
    ro = _FakeResidual(inserted=1, scanned=50)
    cp = _FakeChangepoint(inserted=3, scanned=90)

    summary = run_all_detectors(
        _snap(),
        sensor_failure_rules=sf,
        residual_outlier_detector=ro,
        changepoint_detector=cp,
    )

    assert sf.calls == 1
    assert ro.calls == 1
    assert cp.calls == 1
    assert cp.last_days == 90  # default

    assert isinstance(summary, AnomalyRunSummary)
    assert summary.sensor_failure == 2
    assert summary.residual_outlier == 1
    assert summary.regime_shift == 3
    assert summary.total_inserted == 6
    assert summary.total_scanned == 240


def test_run_all_detectors_re_raises_per_detector_exception() -> None:
    sf = _FakeSensor(inserted=2, scanned=100)
    ro = _FakeResidual(raises=RuntimeError("boom"))
    cp = _FakeChangepoint(inserted=3, scanned=90)

    with pytest.raises(RuntimeError, match="boom"):
        run_all_detectors(
            _snap(),
            sensor_failure_rules=sf,
            residual_outlier_detector=ro,
            changepoint_detector=cp,
        )


# ---------------------------------------------------------------------
# Resilient path — `run_safely`
# ---------------------------------------------------------------------
def test_run_safely_swallows_exceptions_and_returns_zero_for_failed_detector() -> None:
    sf = _FakeSensor(inserted=2, scanned=100)
    ro = _FakeResidual(raises=RuntimeError("synthetic SQL outage"))
    cp = _FakeChangepoint(inserted=3, scanned=90)

    summary = run_safely(
        _snap(),
        sensor_failure_rules=sf,
        residual_outlier_detector=ro,
        changepoint_detector=cp,
    )

    assert summary.sensor_failure == 2  # sensor_failure ran successfully
    assert summary.residual_outlier == 0  # failure → 0
    assert summary.regime_shift == 3  # changepoint ran successfully
    # All three detectors were attempted.
    assert sf.calls == ro.calls == cp.calls == 1


def test_run_safely_continues_after_first_exception() -> None:
    """A failure in `sensor_failure` does NOT short-circuit the others."""
    sf = _FakeSensor(raises=RuntimeError("first failure"))
    ro = _FakeResidual(inserted=4, scanned=50)
    cp = _FakeChangepoint(inserted=2, scanned=30)

    summary = run_safely(
        _snap(),
        sensor_failure_rules=sf,
        residual_outlier_detector=ro,
        changepoint_detector=cp,
    )

    assert summary.sensor_failure == 0
    assert summary.residual_outlier == 4
    assert summary.regime_shift == 2


def test_summary_dataclass_fields_match_wire_contract() -> None:
    """The dataclass field names must match the wire `AnomalyRunSummary`
    schema after Pydantic alias translation.
    """
    summary = AnomalyRunSummary(
        sensor_failure=1,
        residual_outlier=2,
        regime_shift=3,
    )
    assert summary.sensor_failure == 1
    assert summary.residual_outlier == 2
    assert summary.regime_shift == 3
    assert summary.total_inserted == 6
