"""Anomaly orchestrator — runs all three detectors at the current cursor.

Per issue #10:

  ```python
  def run_all_detectors(snap: CursorSnapshot) -> AnomalyRunSummary:
      n1 = sensor_failure_rules.scan_recent(snap)
      n2 = residual_outlier_detector.scan_recent(snap, forecaster)
      n3 = changepoint_detector.rescan_window(snap, days=90)
      return AnomalyRunSummary(failure=n1, outlier=n2, changepoint=n3)
  ```

Per ADR-0011: this is a small private orchestration helper — not a
strategy interface. The three detectors are invoked by name with their
naturally-shaped methods (`scan_recent` for the two point-in-time
detectors, `rescan_window` for the changepoint detector) — the
differing scan windows and idempotency strategies are visible at the
call site.

The orchestrator also exposes a `run_safely` wrapper that swallows
per-detector exceptions so a failure in one detector does NOT silence
the other two. Each per-detector exception is logged with traceback.
"""

from __future__ import annotations

import logging
from collections.abc import Callable
from dataclasses import dataclass
from datetime import timedelta

from .anomaly_changepoint import (
    ChangepointDetector,
    DEFAULT_DAYS as CHANGEPOINT_DEFAULT_DAYS,
)
from .anomaly_residual import ResidualOutlierDetector
from .anomaly_sensor_failure import SensorFailureRules
from .cursor import CursorSnapshot

log = logging.getLogger("climasense_ml.anomaly_orchestrator")


@dataclass(frozen=True)
class AnomalyRunSummary:
    """Per-type breakdown of one orchestrator invocation.

    Maps directly onto the wire contract's
    `AnomalyRunSummary` schema (camelCase fields on the wire).

    `sensor_failure` and `residual_outlier` are net-insert counts (the
    two detectors use `INSERT … WHERE NOT EXISTS`). `regime_shift` is
    the post-replace row count (scan-and-replace yields a stable
    rowset, not a net insert count). The orchestrator does NOT try to
    flatten these semantics — re-running on the same data MUST yield
    a stable `AnomalyRunSummary` for the changepoint detector to be
    idempotent (Golden test 4 locks this).
    """

    sensor_failure: int
    residual_outlier: int
    regime_shift: int
    sensor_failure_scanned: int = 0
    residual_outlier_scanned: int = 0
    regime_shift_scanned: int = 0

    @property
    def total_inserted(self) -> int:
        return self.sensor_failure + self.residual_outlier + self.regime_shift

    @property
    def total_scanned(self) -> int:
        return (
            self.sensor_failure_scanned
            + self.residual_outlier_scanned
            + self.regime_shift_scanned
        )


def run_all_detectors(
    snap: CursorSnapshot,
    *,
    sensor_failure_rules: SensorFailureRules,
    residual_outlier_detector: ResidualOutlierDetector,
    changepoint_detector: ChangepointDetector,
    changepoint_days: int = CHANGEPOINT_DEFAULT_DAYS,
) -> AnomalyRunSummary:
    """Sequence the three detectors and aggregate their per-type counts.

    Each detector runs in its own transaction. If one detector raises,
    the orchestrator re-raises after logging — the caller decides
    whether to swallow (the scheduled job swallows; the on-demand
    endpoint surfaces the failure as 500).
    """

    log.info(
        "run_all_detectors: starting cursor=%s changepoint_days=%d",
        snap.as_of.isoformat(),
        changepoint_days,
    )

    sf = sensor_failure_rules.scan_recent(snap)
    ro = residual_outlier_detector.scan_recent(snap)
    cp = changepoint_detector.rescan_window(snap, days=changepoint_days)

    summary = AnomalyRunSummary(
        sensor_failure=int(sf.inserted),
        residual_outlier=int(ro.inserted),
        regime_shift=int(cp.inserted),
        sensor_failure_scanned=int(sf.scanned),
        residual_outlier_scanned=int(ro.scanned),
        regime_shift_scanned=int(cp.scanned),
    )

    log.info(
        "run_all_detectors: done sensor_failure=%d residual_outlier=%d "
        "regime_shift=%d (total_inserted=%d total_scanned=%d)",
        summary.sensor_failure,
        summary.residual_outlier,
        summary.regime_shift,
        summary.total_inserted,
        summary.total_scanned,
    )

    return summary


def run_safely(
    snap: CursorSnapshot,
    *,
    sensor_failure_rules: SensorFailureRules,
    residual_outlier_detector: ResidualOutlierDetector,
    changepoint_detector: ChangepointDetector,
    changepoint_days: int = CHANGEPOINT_DEFAULT_DAYS,
) -> AnomalyRunSummary:
    """Like `run_all_detectors` but swallows per-detector exceptions.

    Each detector runs in its own try/except. A failure in one
    detector is logged with traceback and reports `0` for that type;
    the other two still run. The scheduler uses this so a transient
    SQL error doesn't poison subsequent ticks.
    """

    sensor_failure_count = 0
    residual_outlier_count = 0
    regime_shift_count = 0
    sensor_failure_scanned = 0
    residual_outlier_scanned = 0
    regime_shift_scanned = 0

    try:
        sf = sensor_failure_rules.scan_recent(snap)
        sensor_failure_count = int(sf.inserted)
        sensor_failure_scanned = int(sf.scanned)
    except Exception:  # noqa: BLE001 — orchestrator wants resilience
        log.exception("SensorFailureRules.scan_recent: failed")

    try:
        ro = residual_outlier_detector.scan_recent(snap)
        residual_outlier_count = int(ro.inserted)
        residual_outlier_scanned = int(ro.scanned)
    except Exception:  # noqa: BLE001
        log.exception("ResidualOutlierDetector.scan_recent: failed")

    try:
        cp = changepoint_detector.rescan_window(snap, days=changepoint_days)
        regime_shift_count = int(cp.inserted)
        regime_shift_scanned = int(cp.scanned)
    except Exception:  # noqa: BLE001
        log.exception("ChangepointDetector.rescan_window: failed")

    return AnomalyRunSummary(
        sensor_failure=sensor_failure_count,
        residual_outlier=residual_outlier_count,
        regime_shift=regime_shift_count,
        sensor_failure_scanned=sensor_failure_scanned,
        residual_outlier_scanned=residual_outlier_scanned,
        regime_shift_scanned=regime_shift_scanned,
    )


__all__ = [
    "AnomalyRunSummary",
    "run_all_detectors",
    "run_safely",
]
