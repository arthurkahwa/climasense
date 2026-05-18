"""Comfort scoring — on-demand and APScheduler-driven β-prime emission.

Mirrors the slice-5 forecast-emitter pattern:

  * `score_at_cursor(engine, snap, hemisphere, hours_window)` — pure
    composition of (a) SQL read of the trailing-hour mean (T, RH)
    visible at the cursor and (b) the pure `ComfortCalculator.score()`.
    Used by `POST /api/comfort/recompute` and the on-demand
    `/api/ml/run/comfort` proxy.

  * `ComfortEmitter` — APScheduler β-prime gate. Every wall-minute the
    scheduler fires `emit_if_due`. The job constructs a fresh
    `CursorSnapshot`, calls `snap.should_emit(last_bucket, 1h)`, and
    persists exactly one `ComfortScores` row per replay-hour.

The scheduler is OPTIONAL — controlled by
`CLIMASENSE_COMFORT_SCHEDULER` (default `on`). Disable for unit
tests via `CLIMASENSE_SKIP_COMFORT_SCHEDULER`.
"""

from __future__ import annotations

import logging
import threading
from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import pandas as pd
from sqlalchemy import text

from .comfort import ComfortCalculator, ComfortScoreResult, Hemisphere
from .comfort_persistence import (
    PersistedComfortRow,
    read_max_bucket_time,
    upsert_comfort_score,
)
from .cursor import CursorSnapshot

log = logging.getLogger("climasense_ml.comfort_emitter")


# One emission per replay-hour. Mirrors the slice-5 forecast cadence.
EMIT_CADENCE: timedelta = timedelta(hours=1)

# Trailing window the score is computed against (mean T + mean RH).
WINDOW_HOURS: int = 1


@dataclass(frozen=True)
class ComfortEmissionResult:
    """Outcome of one comfort-scoring tick."""

    bucket_time: datetime
    result: ComfortScoreResult
    average_temperature: float
    average_humidity: float
    sample_count: int


def _load_trailing_mean(
    engine,  # type: ignore[no-untyped-def]
    as_of: datetime,
    *,
    window_hours: int = WINDOW_HOURS,
) -> tuple[float, float, int]:
    """Mean (T, RH) over the last `window_hours` of `SensorReadings`
    visible at `as_of`. Returns `(mean_t, mean_rh, sample_count)`.

    Empty window: returns `(NaN, NaN, 0)` so the caller can decide
    whether to emit (typically: skip the tick — score 0 is misleading
    when there is no data to score).
    """
    if as_of.tzinfo is None:
        as_of = as_of.replace(tzinfo=timezone.utc)
    window_start = as_of - timedelta(hours=window_hours)

    sql = text(
        """
        SELECT AVG(CAST(Temperature AS FLOAT)) AS mean_t,
               AVG(CAST(Humidity    AS FLOAT)) AS mean_rh,
               COUNT(*)                        AS sample_count
          FROM dbo.SensorReadings
         WHERE ReadingTime >  :start
           AND ReadingTime <= :end
        """
    )
    with engine.connect() as conn:
        row = conn.execute(
            sql,
            {
                "start": window_start.replace(tzinfo=None),
                "end": as_of.replace(tzinfo=None),
            },
        ).fetchone()
    if row is None or row[2] == 0:
        return (float("nan"), float("nan"), 0)
    return (float(row[0]), float(row[1]), int(row[2]))


def score_at_cursor(
    engine,  # type: ignore[no-untyped-def]
    snap: CursorSnapshot,
    *,
    hemisphere: Hemisphere = "N",
    window_hours: int = WINDOW_HOURS,
) -> ComfortEmissionResult | None:
    """Compose one comfort score at the cursor's position.

    Steps:
      1. Pull mean (T, RH) over the last `window_hours` from
         `SensorReadings`.
      2. If the window is empty, return `None` (caller skips).
      3. Call `ComfortCalculator.score()` with the mean inputs.
      4. Return `ComfortEmissionResult` so the caller can persist or
         surface the score.
    """
    mean_t, mean_rh, n = _load_trailing_mean(
        engine, snap.as_of, window_hours=window_hours
    )
    if n == 0 or pd.isna(mean_t) or pd.isna(mean_rh):
        log.info(
            "score_at_cursor: empty window (cursor=%s, window_hours=%d)",
            snap.as_of.isoformat(),
            window_hours,
        )
        return None
    result = ComfortCalculator.score(
        t_c=mean_t,
        rh_pct=mean_rh,
        bucket_time=snap.as_of,
        hemisphere=hemisphere,
    )
    return ComfortEmissionResult(
        bucket_time=snap.as_of,
        result=result,
        average_temperature=mean_t,
        average_humidity=mean_rh,
        sample_count=n,
    )


def emit_comfort(
    engine,  # type: ignore[no-untyped-def]
    snap: CursorSnapshot,
    *,
    hemisphere: Hemisphere = "N",
    window_hours: int = WINDOW_HOURS,
) -> ComfortEmissionResult | None:
    """Compute + persist a comfort score at the cursor.

    Returns the emission result, or `None` if the window was empty.
    Idempotent on `(BucketTime)` via the MERGE in
    `upsert_comfort_score` — re-running at the same cursor replaces
    the prior row with the same value (the calculator is pure).
    """
    emission = score_at_cursor(
        engine, snap, hemisphere=hemisphere, window_hours=window_hours
    )
    if emission is None:
        return None
    upsert_comfort_score(
        engine, bucket_time=emission.bucket_time, result=emission.result
    )
    return emission


# ---------------------------------------------------------------------
# APScheduler-driven β-prime emission gate.
# ---------------------------------------------------------------------
class ComfortEmitter:
    """Wraps `emit_comfort` with a β-prime gate so APScheduler can
    fire it every wall-minute without overproducing rows.

    On each tick:
      1. Construct a fresh `CursorSnapshot` from `clock_provider()`.
      2. Read the last `BucketTime` from `dbo.ComfortScores`.
      3. `snap.should_emit(last, EMIT_CADENCE)` → bool.
      4. If true: call `emit_comfort`. Otherwise skip.

    The hemisphere is captured at construction time (read once from
    the env var). Changing `COMFORT_HEMISPHERE` requires a process
    restart — consistent with the slice-7 deployment model.
    """

    def __init__(
        self,
        *,
        engine,  # type: ignore[no-untyped-def]
        clock_provider: Callable[[], CursorSnapshot],
        hemisphere: Hemisphere = "N",
        window_hours: int = WINDOW_HOURS,
    ) -> None:
        self._engine = engine
        self._clock_provider = clock_provider
        self._hemisphere = hemisphere
        self._window_hours = window_hours
        self._lock = threading.Lock()

    def emit_if_due(self) -> ComfortEmissionResult | None:
        """Single-tick body. Safe to invoke from APScheduler.

        Returns the `ComfortEmissionResult` if a row was emitted, or
        `None` when the β-prime gate held the tick closed (or the
        trailing window had no readings).
        """
        with self._lock:
            snap = self._clock_provider()
            last = read_max_bucket_time(self._engine)
            if not snap.should_emit(last, EMIT_CADENCE):
                log.info(
                    "ComfortEmitter: gate closed (cursor=%s, last=%s, cadence=%s)",
                    snap.as_of.isoformat(),
                    last.isoformat() if last else "never",
                    EMIT_CADENCE,
                )
                return None
            try:
                emission = emit_comfort(
                    self._engine,
                    snap,
                    hemisphere=self._hemisphere,
                    window_hours=self._window_hours,
                )
            except Exception:  # noqa: BLE001 — log and swallow so the scheduler keeps ticking
                log.exception(
                    "ComfortEmitter: emission failed at cursor=%s",
                    snap.as_of.isoformat(),
                )
                return None
            if emission is None:
                log.info(
                    "ComfortEmitter: no readings in window (cursor=%s)",
                    snap.as_of.isoformat(),
                )
                return None
            log.info(
                "ComfortEmitter: emitted bucket=%s score=%.2f rating=%s season=%s",
                emission.bucket_time.isoformat(),
                emission.result.score,
                emission.result.rating,
                emission.result.season,
            )
            return emission


__all__ = [
    "EMIT_CADENCE",
    "WINDOW_HOURS",
    "ComfortEmissionResult",
    "ComfortEmitter",
    "emit_comfort",
    "score_at_cursor",
]
