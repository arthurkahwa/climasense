"""Persistence — read helpers for the slice-8 anomaly surface.

The three detectors own their own INSERT/DELETE statements (each
module is self-contained on the write path). This module owns the
READ side: pulling rows back through the cursor-clipped TVF
`dbo.fv_anomalies_at_cursor(@asOf)` defined in `init-db.sql §3.2`.

Two helpers:

  * `read_recent_rows(engine, snap, since)` — return the anomaly rows
    with `ReadingTime > since` visible at the cursor. Used by the
    `/api/anomalies/detect` response so the caller sees the rows that
    landed in the run window.
  * `read_anomaly_counts_by_type(engine, snap)` — aggregate counts per
    `AnomalyType` over the window. Used by the orchestrator's
    "summary" mode for the dashboard pill.

Both go through the TVF; cursor-clipping is a property of the schema.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import datetime, timezone

from sqlalchemy import text

from .cursor import CursorSnapshot

log = logging.getLogger("climasense_ml.anomaly_persistence")


@dataclass(frozen=True)
class PersistedAnomalyRow:
    anomaly_id: int
    reading_time: datetime
    anomaly_type: str
    severity: float
    score: float | None
    description: str | None
    detected_at: datetime


def _to_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


def _to_naive_utc(d: datetime) -> datetime:
    if d.tzinfo is None:
        return d
    return d.astimezone(timezone.utc).replace(tzinfo=None)


_RECENT_SQL = text(
    """
    SELECT AnomalyId, ReadingTime, AnomalyType, Severity, Score, Description, DetectedAt
      FROM dbo.fv_anomalies_at_cursor(:as_of)
     WHERE ReadingTime >= :since
     ORDER BY ReadingTime DESC;
    """
)


def read_recent_rows(
    engine,  # type: ignore[no-untyped-def]
    *,
    snap: CursorSnapshot,
    since: datetime,
) -> list[PersistedAnomalyRow]:
    """Return rows visible at the cursor with `ReadingTime >= since`.

    Reads through `dbo.fv_anomalies_at_cursor(@asOf)` — cursor-clipping
    is enforced by the schema, not by caller discipline.
    """
    params = {
        "as_of": _to_naive_utc(snap.as_of),
        "since": _to_naive_utc(since),
    }
    with engine.connect() as conn:
        rows = conn.execute(_RECENT_SQL, params).fetchall()
    return [
        PersistedAnomalyRow(
            anomaly_id=int(r[0]),
            reading_time=_to_utc(r[1]),
            anomaly_type=str(r[2]),
            severity=float(r[3]),
            score=(float(r[4]) if r[4] is not None else None),
            description=(str(r[5]) if r[5] is not None else None),
            detected_at=_to_utc(r[6]),
        )
        for r in rows
    ]


_COUNTS_SQL = text(
    """
    SELECT AnomalyType, COUNT(*) AS n
      FROM dbo.fv_anomalies_at_cursor(:as_of)
     WHERE ReadingTime >= :since
     GROUP BY AnomalyType;
    """
)


def read_anomaly_counts_by_type(
    engine,  # type: ignore[no-untyped-def]
    *,
    snap: CursorSnapshot,
    since: datetime,
) -> dict[str, int]:
    """Aggregate count of anomalies per type in `[since, as_of]`."""
    params = {
        "as_of": _to_naive_utc(snap.as_of),
        "since": _to_naive_utc(since),
    }
    with engine.connect() as conn:
        rows = conn.execute(_COUNTS_SQL, params).fetchall()
    return {str(r[0]): int(r[1]) for r in rows}


__all__ = [
    "PersistedAnomalyRow",
    "read_anomaly_counts_by_type",
    "read_recent_rows",
]
