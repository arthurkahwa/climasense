"""SensorFailureRules — rule-based detector for sensor-failure events.

Per ADR-0002 + issue #10, this detector flags three classes of event
inside a 24-hour scan window:

  * **Gap** — `LAG(ReadingTime)` shows a gap of strictly more than 10
    minutes since the previous row. The reading at the *trailing* edge
    of the gap is flagged (the first reading after the gap), with a
    `"gap >NNN min"` description.
  * **Stuck value** — a run of >= 5 consecutive identical (Temperature,
    Humidity) readings. The reading at the *end* of the run is flagged
    once per run, with a `"stuck for N readings"` description.
  * **Out of range** — Temperature outside `[-10, 50]` °C or Humidity
    outside `[0, 100]` %. Each offending row is flagged with a
    `"T=NN out of [-10,50]"` / `"RH=NN out of [0,100]"` description.

All inserts are idempotent via `INSERT … WHERE NOT EXISTS` keyed on
`(AnomalyType, ReadingTime)` — the schema-level UNIQUE constraint
(`init-db.sql §2.2 UQ_Anomalies_TypeTime`) is the structural lock.

Per ADR-0011 (interface-emergence policy): this is a concrete class
with a single naturally-shaped method, `scan_recent(snap)`. There is
NO `IAnomalyStrategy` interface — the three detectors do NOT share a
seam, by design.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

from sqlalchemy import text

from .cursor import CursorSnapshot

log = logging.getLogger("climasense_ml.anomaly_sensor_failure")


# ---------------------------------------------------------------------
# Detector hyperparameters (notebook EDA + ADR-0002).
# ---------------------------------------------------------------------
SCAN_WINDOW: timedelta = timedelta(hours=24)
"""Scan window for `scan_recent`. ADR-0002 / PRD §"Anomalies"."""

GAP_THRESHOLD_MINUTES: int = 10
"""A gap is reported when `LAG(ReadingTime)` differs by > 10 min."""

STUCK_RUN_LENGTH: int = 5
"""Number of consecutive identical (T, RH) rows that count as 'stuck'."""

TEMPERATURE_MIN: float = -10.0
TEMPERATURE_MAX: float = 50.0
HUMIDITY_MIN: float = 0.0
HUMIDITY_MAX: float = 100.0
"""Physically plausible ranges. ADR-0002, also referenced in CONTEXT.md."""

ANOMALY_TYPE: str = "sensor_failure"


# ---------------------------------------------------------------------
# DTO returned by `scan_recent` so callers can log or surface per-detector
# counts. The orchestrator aggregates these into `AnomalyRunSummary`.
# ---------------------------------------------------------------------
@dataclass(frozen=True)
class SensorFailureScanResult:
    inserted: int
    scanned: int
    window_start: datetime
    window_end: datetime


def _to_naive_utc(d: datetime) -> datetime:
    """Coerce to a naive UTC `datetime` for SQL Server `DATETIME2` params.

    SQL Server's `DATETIME2` parameters expect naive values; SQLAlchemy
    won't auto-strip a tz-aware datetime. We normalise at the boundary.
    """
    if d.tzinfo is None:
        return d
    return d.astimezone(timezone.utc).replace(tzinfo=None)


# ---------------------------------------------------------------------
# SQL — each clause is a separate INSERT so we keep the queries simple,
# auditable, and unambiguous about which rule produced each row.
#
# Idempotency: every insert is gated by `WHERE NOT EXISTS (...)` against
# `UQ_Anomalies_TypeTime`. Re-running the same window inserts zero new
# rows.
# ---------------------------------------------------------------------

# Gaps: window function over the scan window. `LAG(ReadingTime)` is the
# prior row's reading time; if the current row is more than
# GAP_THRESHOLD_MINUTES after the prior row, the *current* row is the
# leading edge of the gap and we flag it.
_GAP_SQL = text(
    """
    WITH gaps AS (
        SELECT
            ReadingTime,
            DATEDIFF(SECOND,
                LAG(ReadingTime) OVER (ORDER BY ReadingTime),
                ReadingTime) AS gap_seconds
          FROM dbo.SensorReadings
         WHERE ReadingTime >  :window_start
           AND ReadingTime <= :window_end
    )
    INSERT INTO dbo.Anomalies (ReadingTime, AnomalyType, Severity, Score, Description)
    SELECT  ReadingTime,
            'sensor_failure',
            1.0,
            CAST(gap_seconds / 60.0 AS DECIMAL(8, 4)),
            CONCAT('gap ', CAST(gap_seconds / 60 AS VARCHAR(16)), ' min')
      FROM  gaps g
     WHERE  g.gap_seconds > :gap_threshold_seconds
       AND  NOT EXISTS (
            SELECT 1 FROM dbo.Anomalies a
             WHERE a.AnomalyType = 'sensor_failure'
               AND a.ReadingTime = g.ReadingTime
       );
    """
)


# Stuck values: identify runs of consecutive identical (T, RH) using the
# "gaps and islands" pattern with row_number() differences. We flag the
# *last* row of each run that's at least STUCK_RUN_LENGTH long.
_STUCK_SQL = text(
    """
    WITH ranked AS (
        SELECT
            ReadingTime,
            Temperature,
            Humidity,
            ROW_NUMBER() OVER (ORDER BY ReadingTime) AS rn,
            ROW_NUMBER() OVER (PARTITION BY Temperature, Humidity ORDER BY ReadingTime) AS rn_per_value
          FROM dbo.SensorReadings
         WHERE ReadingTime >  :window_start
           AND ReadingTime <= :window_end
    ),
    runs AS (
        SELECT
            ReadingTime,
            Temperature,
            Humidity,
            rn - rn_per_value AS island_id
          FROM ranked
    ),
    run_ends AS (
        SELECT
            MAX(ReadingTime) AS ReadingTime,
            COUNT(*)         AS run_length,
            MIN(Temperature) AS Temperature,
            MIN(Humidity)    AS Humidity
          FROM runs
         GROUP BY Temperature, Humidity, island_id
        HAVING COUNT(*) >= :stuck_run_length
    )
    INSERT INTO dbo.Anomalies (ReadingTime, AnomalyType, Severity, Score, Description)
    SELECT  r.ReadingTime,
            'sensor_failure',
            1.0,
            CAST(r.run_length AS DECIMAL(8, 4)),
            CONCAT('stuck for ', CAST(r.run_length AS VARCHAR(16)), ' readings')
      FROM  run_ends r
     WHERE  NOT EXISTS (
            SELECT 1 FROM dbo.Anomalies a
             WHERE a.AnomalyType = 'sensor_failure'
               AND a.ReadingTime = r.ReadingTime
       );
    """
)


# Out-of-range: each offending row flagged once. Two physical bounds
# (Temperature, Humidity); a row that breaches both is recorded twice
# is intentional NOT — we emit the temperature breach first via the
# CASE WHEN order, ensuring a stable description.
_RANGE_SQL = text(
    """
    INSERT INTO dbo.Anomalies (ReadingTime, AnomalyType, Severity, Score, Description)
    SELECT  s.ReadingTime,
            'sensor_failure',
            1.0,
            CASE
                WHEN s.Temperature < :t_min OR s.Temperature > :t_max
                    THEN CAST(s.Temperature AS DECIMAL(8, 4))
                ELSE CAST(s.Humidity AS DECIMAL(8, 4))
            END,
            CASE
                WHEN s.Temperature < :t_min OR s.Temperature > :t_max
                    THEN CONCAT('T=', CAST(s.Temperature AS VARCHAR(16)),
                                ' out of [', :t_min_lit, ',', :t_max_lit, ']')
                ELSE CONCAT('RH=', CAST(s.Humidity AS VARCHAR(16)),
                            ' out of [', :rh_min_lit, ',', :rh_max_lit, ']')
            END
      FROM  dbo.SensorReadings s
     WHERE  s.ReadingTime >  :window_start
       AND  s.ReadingTime <= :window_end
       AND  (
            s.Temperature < :t_min OR s.Temperature > :t_max
         OR s.Humidity    < :rh_min OR s.Humidity   > :rh_max
       )
       AND  NOT EXISTS (
            SELECT 1 FROM dbo.Anomalies a
             WHERE a.AnomalyType = 'sensor_failure'
               AND a.ReadingTime = s.ReadingTime
       );
    """
)


# Scanned-row count — used by `AnomalyDetectResponse.totalScanned`.
_COUNT_SQL = text(
    """
    SELECT COUNT(*) FROM dbo.SensorReadings
     WHERE ReadingTime >  :window_start
       AND ReadingTime <= :window_end;
    """
)


class SensorFailureRules:
    """Three-rule sensor-failure detector. See module docstring."""

    def __init__(
        self,
        *,
        engine,  # type: ignore[no-untyped-def]
        gap_threshold_minutes: int = GAP_THRESHOLD_MINUTES,
        stuck_run_length: int = STUCK_RUN_LENGTH,
        temperature_min: float = TEMPERATURE_MIN,
        temperature_max: float = TEMPERATURE_MAX,
        humidity_min: float = HUMIDITY_MIN,
        humidity_max: float = HUMIDITY_MAX,
    ) -> None:
        self._engine = engine
        self._gap_threshold_minutes = int(gap_threshold_minutes)
        self._stuck_run_length = int(stuck_run_length)
        self._temperature_min = float(temperature_min)
        self._temperature_max = float(temperature_max)
        self._humidity_min = float(humidity_min)
        self._humidity_max = float(humidity_max)

    # -----------------------------------------------------------------
    def scan_recent(self, snap: CursorSnapshot) -> SensorFailureScanResult:
        """Run all three rules over `snap.windowed(24h)` and return a count.

        Idempotent — re-running on the same window inserts zero new
        rows because every INSERT is gated by `WHERE NOT EXISTS`.
        """

        start, end = snap.windowed(SCAN_WINDOW)
        params_window = {
            "window_start": _to_naive_utc(start),
            "window_end": _to_naive_utc(end),
        }

        with self._engine.begin() as conn:
            scanned = int(conn.execute(_COUNT_SQL, params_window).scalar() or 0)

            gap_inserted = conn.execute(
                _GAP_SQL,
                {
                    **params_window,
                    "gap_threshold_seconds": self._gap_threshold_minutes * 60,
                },
            ).rowcount
            stuck_inserted = conn.execute(
                _STUCK_SQL,
                {
                    **params_window,
                    "stuck_run_length": self._stuck_run_length,
                },
            ).rowcount
            range_inserted = conn.execute(
                _RANGE_SQL,
                {
                    **params_window,
                    "t_min": self._temperature_min,
                    "t_max": self._temperature_max,
                    "rh_min": self._humidity_min,
                    "rh_max": self._humidity_max,
                    "t_min_lit": str(self._temperature_min),
                    "t_max_lit": str(self._temperature_max),
                    "rh_min_lit": str(self._humidity_min),
                    "rh_max_lit": str(self._humidity_max),
                },
            ).rowcount

        inserted = max(gap_inserted, 0) + max(stuck_inserted, 0) + max(range_inserted, 0)

        log.info(
            "SensorFailureRules.scan_recent: window=(%s, %s] scanned=%d "
            "gap_inserted=%d stuck_inserted=%d range_inserted=%d",
            start.isoformat(),
            end.isoformat(),
            scanned,
            max(gap_inserted, 0),
            max(stuck_inserted, 0),
            max(range_inserted, 0),
        )

        return SensorFailureScanResult(
            inserted=inserted,
            scanned=scanned,
            window_start=start,
            window_end=end,
        )


__all__ = [
    "ANOMALY_TYPE",
    "GAP_THRESHOLD_MINUTES",
    "HUMIDITY_MAX",
    "HUMIDITY_MIN",
    "SCAN_WINDOW",
    "STUCK_RUN_LENGTH",
    "SensorFailureRules",
    "SensorFailureScanResult",
    "TEMPERATURE_MAX",
    "TEMPERATURE_MIN",
]
