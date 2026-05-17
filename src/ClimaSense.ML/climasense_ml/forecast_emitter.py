"""Forecast emission — both on-demand (HTTP) and scheduled (APScheduler).

Two responsibilities:

  * `emit_forecast(forecaster, engine, snap, horizon_hours)` — load the
    tail history from `SensorReadings`, run `forecaster.predict`,
    persist via `forecast_persistence.persist_forecast`, return the
    envelope. Used by `POST /api/forecast`.

  * `ForecastEmitter` — APScheduler-driven β-prime emission gate. Every
    wall-minute the scheduler fires `_emit_if_due`. The job constructs
    a fresh `CursorSnapshot`, calls `snap.should_emit(last_emit,
    timedelta(hours=1))`, and emits exactly one batch per replay-hour.

The scheduler is OPTIONAL — slice 5 ships the on-demand HTTP path
unconditionally and the scheduler under `CLIMASENSE_FORECAST_SCHEDULER`
(default `on`). The slice-12 ReplayClock will exercise both.
"""

from __future__ import annotations

import logging
import threading
from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import pandas as pd
from sqlalchemy import text

from .cursor import CursorSnapshot
from .forecast_persistence import (
    PersistedForecastRow,
    persist_forecast,
    read_max_generated_at,
)
from .forecaster import LAGS, LagLinearForecaster

log = logging.getLogger("climasense_ml.forecast_emitter")


# Cadence at which the β-prime gate opens — one emission per replay-hour.
EMIT_CADENCE: timedelta = timedelta(hours=1)

# Tail length we pull from SensorReadings for `predict()`. `max(LAGS)`
# hours covers every lag; we pull 2× that to absorb gaps / interpolation
# warm-up.
_TAIL_HOURS: int = max(LAGS) * 2


@dataclass(frozen=True)
class EmissionResult:
    generated_at: datetime
    horizon_hours: int
    persisted_rows: list[PersistedForecastRow]


def _load_tail_history(
    engine,  # type: ignore[no-untyped-def]
    as_of: datetime,
    *,
    tail_hours: int = _TAIL_HOURS,
) -> pd.DataFrame:
    """Read the trailing `tail_hours` of hourly readings up to `as_of`.

    Uses the raw `SensorReadings` table clipped at the cursor.
    Resamples to hourly + linear interpolation so the result is on the
    same grid the forecaster was fitted on.
    """
    if as_of.tzinfo is None:
        as_of = as_of.replace(tzinfo=timezone.utc)
    window_start = as_of - timedelta(hours=tail_hours + max(LAGS))

    sql = text(
        """
        SELECT ReadingTime,
               CAST(Temperature AS FLOAT) AS Temperature,
               CAST(Humidity    AS FLOAT) AS Humidity
          FROM dbo.SensorReadings
         WHERE ReadingTime >  :start
           AND ReadingTime <= :end
         ORDER BY ReadingTime
        """
    )
    with engine.connect() as conn:
        df = pd.read_sql(
            sql,
            conn,
            params={
                "start": window_start.replace(tzinfo=None),
                "end": as_of.replace(tzinfo=None),
            },
            parse_dates=["ReadingTime"],
        )
    if df.empty:
        return pd.DataFrame(columns=["temperature", "humidity"])
    df = df.rename(
        columns={
            "ReadingTime": "timestamp",
            "Temperature": "temperature",
            "Humidity": "humidity",
        }
    ).set_index("timestamp")
    try:
        df_h = df.resample("h").mean().interpolate(method="time", limit_direction="both")
    except ValueError:
        df_h = df.resample("H").mean().interpolate(method="time", limit_direction="both")
    if df_h.index.tz is None:
        df_h.index = df_h.index.tz_localize("UTC")
    # Keep only the last `tail_hours` rows so the predict input is a
    # tight tail (drops the interpolation warm-up that doesn't
    # contribute to any lag).
    return df_h.tail(tail_hours)


def emit_forecast(
    forecaster: LagLinearForecaster,
    engine,  # type: ignore[no-untyped-def]
    snap: CursorSnapshot,
    horizon_hours: int,
) -> EmissionResult:
    """Run one forecast emission cycle.

    Steps:
      1. Pull the trailing `_TAIL_HOURS` of hourly readings at `snap.as_of`.
      2. Call `forecaster.predict(tail, horizon_hours)`.
      3. Persist via `persist_forecast(generated_at=snap.as_of, ...)`.
      4. Return the envelope.

    Raises `RuntimeError` if the tail is too short for the lag set.
    """
    tail = _load_tail_history(engine, snap.as_of)
    if len(tail) < max(LAGS):
        raise RuntimeError(
            f"Forecast emission requires at least {max(LAGS)} hourly rows; "
            f"the tail at cursor {snap.as_of.isoformat()} has {len(tail)}."
        )
    points = forecaster.predict(tail, horizon_hours)
    persisted = persist_forecast(
        engine,
        generated_at=snap.as_of,
        points=points,
        model_version=forecaster.model_version,
    )
    return EmissionResult(
        generated_at=snap.as_of,
        horizon_hours=horizon_hours,
        persisted_rows=persisted,
    )


# ---------------------------------------------------------------------
# APScheduler-driven β-prime emission gate.
# ---------------------------------------------------------------------
class ForecastEmitter:
    """Wraps the on-demand `emit_forecast` with a β-prime gate so
    APScheduler can fire it every wall-minute without overproducing
    rows.

    On each tick:
      1. Construct a fresh `CursorSnapshot` from `clock_provider()`.
      2. Read the last `GeneratedAt` from the DB.
      3. `snap.should_emit(last, EMIT_CADENCE)` → bool.
      4. If true: call `emit_forecast`. Otherwise skip.
    """

    def __init__(
        self,
        *,
        forecaster: LagLinearForecaster,
        engine,  # type: ignore[no-untyped-def]
        clock_provider: Callable[[], CursorSnapshot],
        horizon_hours: int = 72,
    ) -> None:
        self._forecaster = forecaster
        self._engine = engine
        self._clock_provider = clock_provider
        self._horizon_hours = horizon_hours
        self._lock = threading.Lock()

    def emit_if_due(self) -> EmissionResult | None:
        """Single-tick body. Safe to invoke from APScheduler.

        Returns the `EmissionResult` if a row was emitted, or `None`
        when the β-prime gate held the tick closed.
        """
        # The lock ensures concurrent APScheduler ticks (shouldn't
        # happen with the default executor, but defensive) don't both
        # try to emit at the same cursor.
        with self._lock:
            snap = self._clock_provider()
            last = read_max_generated_at(self._engine)
            if not snap.should_emit(last, EMIT_CADENCE):
                log.info(
                    "ForecastEmitter: gate closed (cursor=%s, last=%s, cadence=%s)",
                    snap.as_of.isoformat(),
                    last.isoformat() if last else "never",
                    EMIT_CADENCE,
                )
                return None
            try:
                result = emit_forecast(
                    self._forecaster,
                    self._engine,
                    snap,
                    self._horizon_hours,
                )
            except Exception:  # noqa: BLE001 — log and swallow so the scheduler keeps ticking
                log.exception(
                    "ForecastEmitter: emission failed at cursor=%s",
                    snap.as_of.isoformat(),
                )
                return None
            log.info(
                "ForecastEmitter: emitted %d rows (cursor=%s)",
                len(result.persisted_rows),
                snap.as_of.isoformat(),
            )
            return result


__all__ = [
    "EMIT_CADENCE",
    "EmissionResult",
    "emit_forecast",
    "ForecastEmitter",
]
