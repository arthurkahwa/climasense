"""ChangepointDetector — PELT-based regime-shift detection.

Per ADR-0002 + issue #10:

  * Scan window: 90 days (default — overridable via `rescan_window`'s
    `days` parameter).
  * Series: daily mean temperature over the window.
  * Algorithm: PELT (Pruned Exact Linear Time, `ruptures.Pelt`) with
    `model="rbf"` — kernel-based segmentation that detects mean +
    variance shifts. The cost function's `pen` (penalty) parameter is
    the load-bearing hyperparameter; we default to `pen=10` which is
    the value that surfaces the visible regime shifts in the notebook's
    daily-mean overview plot (see notebook §5 — "Inter-year variation"
    and the segment markers on the 10-year overview).
  * Idempotency strategy — **scan-and-replace** inside a transaction:
        BEGIN TRAN
            sp_getapplock @Resource='changepoint_scan' (exclusive)
            DELETE FROM Anomalies
             WHERE AnomalyType='regime_shift'
               AND ReadingTime BETWEEN @scan_start AND @as_of
            INSERT … one row per detected changepoint
        COMMIT
    PELT is deterministic for the same input — re-running yields the
    same rowset. The `sp_getapplock` guard prevents the nightly job
    and an on-demand button-press from racing.

Per ADR-0011: concrete class, no `IAnomalyStrategy` interface. The
naturally-shaped method name is `rescan_window(snap, days=90)` —
deliberately different from `scan_recent` so the call site reflects
the differing scan window and idempotency model.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import numpy as np
import pandas as pd
from sqlalchemy import text

from .cursor import CursorSnapshot
from .forecaster import load_hourly_from_sql

log = logging.getLogger("climasense_ml.anomaly_changepoint")


# ---------------------------------------------------------------------
# Hyperparameters. The `pen` value is the load-bearing tuning knob.
# ---------------------------------------------------------------------
DEFAULT_DAYS: int = 90
"""Scan window in days. ADR-0002 / PRD §"Anomalies" pin."""

DEFAULT_PELT_PENALTY: float = 10.0
"""PELT penalty parameter for `Pelt(model='rbf')`.

Higher values produce fewer (more conservative) changepoints. Empirical
sweep on a synthetic series with a 4°C mean shift at index 500 finds
`pen=10` reliably picks up the shift without false positives on flat
segments. Documented as a judgment call in the slice 8 PR.
"""

MIN_DAILY_POINTS: int = 14
"""Minimum number of daily-mean points required to run PELT.

The detector is a no-op (returns count=0) on narrower windows because
PELT's `min_size` defaults to 2 and we want at least a week of context
before declaring a changepoint.
"""

ANOMALY_TYPE: str = "regime_shift"
APPLOCK_RESOURCE: str = "changepoint_scan"


@dataclass(frozen=True)
class ChangepointScanResult:
    """Outcome of a single `rescan_window` call.

    `inserted` is the *post-replace* row count (scan-and-replace yields
    a stable rowset, not a net insert count). `replaced` counts the
    rows deleted before the new inserts landed.
    """

    inserted: int
    scanned: int
    window_start: datetime
    window_end: datetime
    replaced: int


def _to_naive_utc(d: datetime) -> datetime:
    if d.tzinfo is None:
        return d
    return d.astimezone(timezone.utc).replace(tzinfo=None)


def _ensure_utc(d: datetime) -> datetime:
    if d.tzinfo is None:
        return d.replace(tzinfo=timezone.utc)
    return d.astimezone(timezone.utc)


# ---------------------------------------------------------------------
# SQL — the scan-and-replace transaction is composed of three steps
# inside a single `BEGIN TRAN` block. We use `sp_getapplock` (exclusive,
# session-scoped) so concurrent invocations serialise rather than
# producing inconsistent intermediate state.
# ---------------------------------------------------------------------

_APPLOCK_ACQUIRE_SQL = text(
    """
    DECLARE @lock_result INT;
    EXEC @lock_result = sp_getapplock
        @Resource = :resource,
        @LockMode = 'Exclusive',
        @LockOwner = 'Transaction',
        @LockTimeout = 30000;
    SELECT @lock_result;
    """
)


_DELETE_SQL = text(
    """
    DELETE FROM dbo.Anomalies
     WHERE AnomalyType = 'regime_shift'
       AND ReadingTime >= :window_start
       AND ReadingTime <= :window_end;
    """
)


_INSERT_SQL = text(
    """
    INSERT INTO dbo.Anomalies (ReadingTime, AnomalyType, Severity, Score, Description)
    VALUES (:reading_time, 'regime_shift', :severity, :score, :description);
    """
)


def _detect_changepoints(
    daily_means: pd.Series,
    *,
    penalty: float,
) -> list[int]:
    """Return the list of changepoint *indices* (one per detected break).

    The `ruptures.Pelt` API returns breakpoints in 1-based index form
    with the final element equal to `len(series)`; we drop the trailing
    sentinel and shift down by one so the returned indices point at the
    first daily-mean row of each new segment.
    """
    import ruptures as rpt  # imported lazily so test fixtures can skip ruptures

    if len(daily_means) < MIN_DAILY_POINTS:
        return []

    signal = daily_means.to_numpy().reshape(-1, 1).astype(float)
    algo = rpt.Pelt(model="rbf").fit(signal)
    breakpoints = algo.predict(pen=float(penalty))
    # Drop the trailing sentinel (always == n) and shift to 0-based.
    return [int(bp) - 1 for bp in breakpoints if bp < len(daily_means)]


class ChangepointDetector:
    """PELT-based changepoint detector for regime-shift anomalies.

    Constructor params:
      * `engine` — SQLAlchemy `Engine`.
      * `penalty` — PELT penalty; overridable per-construction.
      * `daily_loader` — test seam returning a UTC-indexed daily-mean
        DataFrame with at least column `temperature`. Production
        defaults to pulling from `dbo.SensorReadings` via the slice-5
        loader (resampled to daily means).
    """

    def __init__(
        self,
        *,
        engine,  # type: ignore[no-untyped-def]
        penalty: float = DEFAULT_PELT_PENALTY,
        daily_loader=None,  # type: ignore[no-untyped-def]
    ) -> None:
        self._engine = engine
        self._penalty = float(penalty)
        # Test seam — production passes a SQL-backed loader; tests
        # inject a synthetic frame.
        self._daily_loader = daily_loader or self._default_daily_loader

    # -----------------------------------------------------------------
    def _default_daily_loader(self) -> pd.DataFrame:
        """Pull hourly history from SQL and resample to a daily-mean frame."""
        history = load_hourly_from_sql(self._engine)
        if history.empty:
            return history
        return history.resample("1D").mean()

    # -----------------------------------------------------------------
    def rescan_window(
        self,
        snap: CursorSnapshot,
        days: int = DEFAULT_DAYS,
    ) -> ChangepointScanResult:
        """Scan-and-replace the last `days` days of `regime_shift` rows.

        Wrapped in a single transaction with `sp_getapplock` so
        concurrent invocations serialise rather than race.
        """

        if days <= 0:
            raise ValueError("days must be strictly positive")

        end_utc = _ensure_utc(snap.as_of)
        start_utc = end_utc - timedelta(days=days)

        daily = self._daily_loader()
        if daily is None or daily.empty:
            log.info(
                "ChangepointDetector.rescan_window: empty daily series; nothing to do"
            )
            # Still run the DELETE so a previously-detected stale window
            # gets cleared (the cursor moved past the historical data).
            return self._delete_only(start_utc, end_utc, scanned=0)

        # Normalise tz so slicing works.
        if daily.index.tz is None:
            daily = daily.copy()
            daily.index = daily.index.tz_localize("UTC")
        else:
            daily.index = daily.index.tz_convert("UTC")

        window_mask = (daily.index >= pd.Timestamp(start_utc)) & (
            daily.index <= pd.Timestamp(end_utc)
        )
        windowed = daily.loc[window_mask]
        scanned = int(len(windowed))

        if "temperature" not in windowed.columns or windowed.empty:
            log.info(
                "ChangepointDetector.rescan_window: empty windowed slice "
                "(window=(%s, %s], days=%d); nothing to do",
                start_utc.isoformat(),
                end_utc.isoformat(),
                days,
            )
            return self._delete_only(start_utc, end_utc, scanned=scanned)

        # PELT runs purely in Python; transient.
        breakpoints = _detect_changepoints(
            windowed["temperature"], penalty=self._penalty
        )

        # Build the rows to insert. The `ReadingTime` is the first
        # daily-mean of each new segment.
        rows_to_insert: list[dict[str, object]] = []
        prior_mean: float | None = (
            float(windowed["temperature"].iloc[0]) if not windowed.empty else None
        )
        for bp_idx in breakpoints:
            if bp_idx <= 0 or bp_idx >= len(windowed):
                continue
            change_ts = windowed.index[bp_idx].to_pydatetime()
            segment_mean = float(windowed["temperature"].iloc[bp_idx:].mean())
            severity = (
                float(abs(segment_mean - prior_mean)) if prior_mean is not None else 1.0
            )
            description = (
                f"PELT changepoint: mean shift "
                f"{prior_mean:.2f} -> {segment_mean:.2f} °C"
                if prior_mean is not None
                else "PELT changepoint detected"
            )
            rows_to_insert.append(
                {
                    "reading_time": _to_naive_utc(change_ts),
                    "severity": max(severity, 0.0),
                    "score": float(segment_mean),
                    "description": description,
                }
            )
            prior_mean = segment_mean

        # Transactional scan-and-replace inside an applock-guarded scope.
        with self._engine.begin() as conn:
            # Acquire the lock; raise if it can't be obtained within the
            # 30-s timeout. The lock auto-releases on transaction end.
            lock_result = conn.execute(
                _APPLOCK_ACQUIRE_SQL, {"resource": APPLOCK_RESOURCE}
            ).scalar()
            if lock_result is None or int(lock_result) < 0:
                raise RuntimeError(
                    f"ChangepointDetector: sp_getapplock returned "
                    f"{lock_result!r} for resource {APPLOCK_RESOURCE!r} "
                    f"(expected >= 0). Possible deadlock or timeout."
                )

            delete_result = conn.execute(
                _DELETE_SQL,
                {
                    "window_start": _to_naive_utc(start_utc),
                    "window_end": _to_naive_utc(end_utc),
                },
            )
            replaced = max(getattr(delete_result, "rowcount", 0), 0)

            inserted = 0
            for params in rows_to_insert:
                result = conn.execute(_INSERT_SQL, params)
                inserted += max(getattr(result, "rowcount", 1), 0)

        log.info(
            "ChangepointDetector.rescan_window: window=(%s, %s] days=%d "
            "scanned=%d replaced=%d inserted=%d penalty=%.2f",
            start_utc.isoformat(),
            end_utc.isoformat(),
            days,
            scanned,
            replaced,
            inserted,
            self._penalty,
        )

        return ChangepointScanResult(
            inserted=inserted,
            scanned=scanned,
            window_start=start_utc,
            window_end=end_utc,
            replaced=replaced,
        )

    # -----------------------------------------------------------------
    def _delete_only(
        self,
        start_utc: datetime,
        end_utc: datetime,
        *,
        scanned: int,
    ) -> ChangepointScanResult:
        """Clear stale `regime_shift` rows in the window without inserting."""
        with self._engine.begin() as conn:
            conn.execute(
                _APPLOCK_ACQUIRE_SQL, {"resource": APPLOCK_RESOURCE}
            ).scalar()
            delete_result = conn.execute(
                _DELETE_SQL,
                {
                    "window_start": _to_naive_utc(start_utc),
                    "window_end": _to_naive_utc(end_utc),
                },
            )
            replaced = max(getattr(delete_result, "rowcount", 0), 0)
        return ChangepointScanResult(
            inserted=0,
            scanned=scanned,
            window_start=start_utc,
            window_end=end_utc,
            replaced=replaced,
        )


__all__ = [
    "ANOMALY_TYPE",
    "APPLOCK_RESOURCE",
    "ChangepointDetector",
    "ChangepointScanResult",
    "DEFAULT_DAYS",
    "DEFAULT_PELT_PENALTY",
    "MIN_DAILY_POINTS",
]
