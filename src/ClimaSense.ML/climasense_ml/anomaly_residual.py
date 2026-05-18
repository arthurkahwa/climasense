"""ResidualOutlierDetector — model-driven anomaly detection.

Consumes the slice-5 `LagLinearForecaster` and flags rows whose
observed value diverges from the forecaster's expectation by more than
`z_threshold` rolling standard deviations.

Per ADR-0002 + issue #10:

  * Scan window: 24h (via `snap.windowed(SCAN_WINDOW)`).
  * For each hourly bucket in the window:
      ŷ_t = predicted by `LagLinearForecaster.predict(history_tail, 1)`
            using the history strictly before `t`.
  * `residual = y_t − ŷ_t`. `rolling_sigma` is the standard deviation
    of the prior `ROLLING_WINDOW_HOURS` residuals.
  * `severity = |residual| / rolling_sigma`. Flag rows where
    `severity > z_threshold`.
  * Idempotent insert via `WHERE NOT EXISTS` keyed on
    `(AnomalyType, ReadingTime)` against `UQ_Anomalies_TypeTime`.

Per ADR-0011: concrete class, no `IAnomalyStrategy` interface. The
forecaster is passed in explicitly (not hidden behind a seam) so the
dependency is visible at the call site.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import numpy as np
import pandas as pd
from sqlalchemy import text

from .cursor import CursorSnapshot
from .forecaster import LAGS, LagLinearForecaster, load_hourly_from_sql

log = logging.getLogger("climasense_ml.anomaly_residual")


# ---------------------------------------------------------------------
# Hyperparameters.
# ---------------------------------------------------------------------
SCAN_WINDOW: timedelta = timedelta(hours=24)
"""Scan window for `scan_recent`. ADR-0002 / PRD §"Anomalies"."""

DEFAULT_Z_THRESHOLD: float = 3.0
"""z-score threshold above which a row is flagged. Notebook EDA §6.5."""

ROLLING_WINDOW_HOURS: int = 48
"""Window for the rolling σ used to standardise residuals."""

ANOMALY_TYPE: str = "residual_outlier"


@dataclass(frozen=True)
class ResidualOutlierScanResult:
    inserted: int
    scanned: int
    window_start: datetime
    window_end: datetime
    flagged: list[tuple[datetime, float, float]]
    """Per-flag triples `(reading_time, residual, severity)` for logging."""


# ---------------------------------------------------------------------
# Insert SQL — one row per flagged hour. Idempotent via NOT EXISTS.
# ---------------------------------------------------------------------
_INSERT_SQL = text(
    """
    INSERT INTO dbo.Anomalies (ReadingTime, AnomalyType, Severity, Score, Description)
    SELECT
        :reading_time,
        'residual_outlier',
        :severity,
        :residual,
        :description
     WHERE NOT EXISTS (
        SELECT 1 FROM dbo.Anomalies a
         WHERE a.AnomalyType = 'residual_outlier'
           AND a.ReadingTime = :reading_time
     );
    """
)


def _to_naive_utc(d: datetime) -> datetime:
    if d.tzinfo is None:
        return d
    return d.astimezone(timezone.utc).replace(tzinfo=None)


def _ensure_utc(d: datetime) -> datetime:
    if d.tzinfo is None:
        return d.replace(tzinfo=timezone.utc)
    return d.astimezone(timezone.utc)


class ResidualOutlierDetector:
    """Model-driven outlier detector. See module docstring."""

    def __init__(
        self,
        *,
        engine,  # type: ignore[no-untyped-def]
        forecaster: LagLinearForecaster,
        z_threshold: float = DEFAULT_Z_THRESHOLD,
        rolling_window_hours: int = ROLLING_WINDOW_HOURS,
        history_loader=None,  # type: ignore[no-untyped-def]
    ) -> None:
        if not forecaster.fitted:
            raise ValueError(
                "ResidualOutlierDetector: forecaster must be boot-fitted "
                "before construction (LagLinearForecaster.fit_at_startup)."
            )
        self._engine = engine
        self._forecaster = forecaster
        self._z_threshold = float(z_threshold)
        self._rolling_window_hours = int(rolling_window_hours)
        # Test seam — production passes the SQL loader; tests inject a
        # synthetic frame so they don't need SQL Server.
        self._history_loader = history_loader or (
            lambda: load_hourly_from_sql(self._engine)
        )

    # -----------------------------------------------------------------
    def scan_recent(self, snap: CursorSnapshot) -> ResidualOutlierScanResult:
        """Scan the trailing 24h for residual outliers; idempotent insert.

        Returns a count of newly-inserted rows (re-run on the same
        window inserts zero new rows). The forecaster is consulted in a
        recursive one-step pattern: each prediction uses only history
        strictly before its target hour.
        """

        start, end = snap.windowed(SCAN_WINDOW)
        start_utc = _ensure_utc(start)
        end_utc = _ensure_utc(end)

        history = self._history_loader()
        if history is None or history.empty:
            log.info(
                "ResidualOutlierDetector.scan_recent: empty history; nothing to do"
            )
            return ResidualOutlierScanResult(
                inserted=0,
                scanned=0,
                window_start=start_utc,
                window_end=end_utc,
                flagged=[],
            )

        # Normalise the history index to UTC for slicing.
        if history.index.tz is None:
            history = history.copy()
            history.index = history.index.tz_localize("UTC")
        else:
            history = history.tz_convert("UTC") if hasattr(history, "tz_convert") else history

        # Hours we want to score: every observed bucket inside (start, end].
        scoring_index = history.index[
            (history.index > pd.Timestamp(start_utc))
            & (history.index <= pd.Timestamp(end_utc))
        ]

        if scoring_index.empty:
            log.info(
                "ResidualOutlierDetector.scan_recent: window=(%s, %s] "
                "has no observed buckets",
                start_utc.isoformat(),
                end_utc.isoformat(),
            )
            return ResidualOutlierScanResult(
                inserted=0,
                scanned=0,
                window_start=start_utc,
                window_end=end_utc,
                flagged=[],
            )

        max_lag = max(LAGS)
        flagged: list[tuple[datetime, float, float, str]] = []
        residual_buffer: list[float] = []

        for target_ts in scoring_index:
            # History strictly before the target hour. Must cover at
            # least `max_lag` hours so every lag is defined.
            tail = history.loc[history.index < target_ts]
            if len(tail) < max_lag:
                continue

            # `predict` accepts the most-recent `max_lag` rows and the
            # target start_time. We ask for a single step and read its
            # `predicted_temperature`.
            prediction = self._forecaster.predict(
                tail.iloc[-max_lag:],
                horizon_hours=1,
                start_time=target_ts.to_pydatetime(),
            )
            y_hat = float(prediction["predicted_temperature"].iloc[0])
            y_actual = float(history.loc[target_ts, "temperature"])
            residual = y_actual - y_hat

            # Rolling σ over the previous `rolling_window_hours` residuals.
            recent = residual_buffer[-self._rolling_window_hours :]
            if len(recent) >= 2:
                sigma = float(np.std(recent, ddof=1))
            else:
                # Bootstrap σ from the forecaster's in-sample residual
                # std until we've accumulated enough rolling residuals.
                sigma = (
                    self._forecaster.summary.temperature_residual_std
                    if self._forecaster.summary is not None
                    else 1.0
                )
            sigma = max(sigma, 1e-9)
            severity = abs(residual) / sigma
            residual_buffer.append(residual)

            if severity > self._z_threshold:
                description = (
                    f"|residual|={abs(residual):.3f}, σ={sigma:.3f}, "
                    f"z={severity:.2f}"
                )
                flagged.append((target_ts.to_pydatetime(), residual, severity, description))

        scanned = int(len(scoring_index))
        inserted = 0
        with self._engine.begin() as conn:
            for (reading_time, residual, severity, description) in flagged:
                result = conn.execute(
                    _INSERT_SQL,
                    {
                        "reading_time": _to_naive_utc(reading_time),
                        "severity": float(severity),
                        "residual": float(residual),
                        "description": description,
                    },
                )
                # SQL Server returns rowcount=1 on a match-and-insert,
                # 0 on a NOT EXISTS skip.
                inserted += max(getattr(result, "rowcount", 0), 0)

        log.info(
            "ResidualOutlierDetector.scan_recent: window=(%s, %s] scanned=%d "
            "flagged=%d inserted=%d z_threshold=%.2f",
            start_utc.isoformat(),
            end_utc.isoformat(),
            scanned,
            len(flagged),
            inserted,
            self._z_threshold,
        )

        return ResidualOutlierScanResult(
            inserted=inserted,
            scanned=scanned,
            window_start=start_utc,
            window_end=end_utc,
            flagged=[(rt, r, s) for (rt, r, s, _d) in flagged],
        )


__all__ = [
    "ANOMALY_TYPE",
    "DEFAULT_Z_THRESHOLD",
    "ROLLING_WINDOW_HOURS",
    "SCAN_WINDOW",
    "ResidualOutlierDetector",
    "ResidualOutlierScanResult",
]
