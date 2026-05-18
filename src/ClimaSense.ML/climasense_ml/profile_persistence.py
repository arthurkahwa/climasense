"""Persistence for `DayProfiles` rows (slice 9).

Three operations, mirroring `comfort_persistence.py`:

  * `merge_day_profiles(engine, rows)` — MERGE one `dbo.DayProfiles`
    row per `DayProfileRow`. Idempotent on `(Date)` via
    `UQ_DayProfiles_Date`. The empirical `Pattern` label is computed
    SQL-side using `dbo.fn_classify_pattern` (init-db.sql §4).
  * `read_day_profiles_at_cursor(engine, snap, start, end)` — return
    profile rows in `[start, end]` visible at the cursor, via the
    inline TVF `dbo.fv_dayprofiles_at_cursor(@asOf)`.
  * `read_max_profile_date(engine)` — the largest `Date` in
    `dbo.DayProfiles` (no cursor clip). Used by tests to confirm
    idempotency.

Schema (init-db.sql §2.4):

    CREATE TABLE dbo.DayProfiles (
        DayProfileId   BIGINT IDENTITY(1,1) NOT NULL,
        [Date]         DATE NOT NULL,
        DayOfWeek      TINYINT NOT NULL,
        MeanResidual   DECIMAL(8, 4) NOT NULL,
        MaxAbsZscore   DECIMAL(8, 4) NOT NULL,
        Pattern        VARCHAR(16) NOT NULL,
        ComputedAt     DATETIME2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_DayProfiles_Date UNIQUE ([Date]),
        CONSTRAINT CK_DayProfiles_Pattern CHECK
            (Pattern IN ('quiet', 'warm', 'cool', 'volatile')),
        CONSTRAINT CK_DayProfiles_DayOfWeek CHECK (DayOfWeek BETWEEN 0 AND 6)
    );
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import date, datetime, timezone

from sqlalchemy import text

from .cursor import CursorSnapshot
from .profile_computer import DayProfileRow

log = logging.getLogger("climasense_ml.profile_persistence")


@dataclass(frozen=True)
class PersistedDayProfileRow:
    """One `dbo.DayProfiles` row read back from SQL."""

    day_profile_id: int
    date: date
    day_of_week: int
    mean_residual: float
    max_abs_zscore: float
    pattern: str
    computed_at: datetime


def _to_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


def _to_naive_utc(d: datetime) -> datetime:
    if d.tzinfo is None:
        return d
    return d.astimezone(timezone.utc).replace(tzinfo=None)


# MERGE one row keyed on Date. Pattern is computed SQL-side via
# `dbo.fn_classify_pattern` (init-db.sql §4) which embeds the
# empirical p90 / p25 / p75 thresholds — the caller passes the two
# numeric inputs and SQL labels them. This keeps the threshold
# constants out of the Python codebase: they live in init-db.sql
# alongside their provenance comment.
_MERGE_SQL = text(
    """
    MERGE dbo.DayProfiles AS target
    USING (
        SELECT
            CAST(:date_value AS DATE)               AS [Date],
            CAST(:day_of_week AS TINYINT)           AS DayOfWeek,
            CAST(:mean_residual AS DECIMAL(8,4))    AS MeanResidual,
            CAST(:max_abs_zscore AS DECIMAL(8,4))   AS MaxAbsZscore,
            (SELECT Pattern
               FROM dbo.fn_classify_pattern(
                        CAST(:mean_residual AS DECIMAL(8,4)),
                        CAST(:max_abs_zscore AS DECIMAL(8,4))
                    )
            ) AS Pattern
    ) AS src
       ON target.[Date] = src.[Date]
    WHEN MATCHED THEN
        UPDATE SET DayOfWeek    = src.DayOfWeek,
                   MeanResidual = src.MeanResidual,
                   MaxAbsZscore = src.MaxAbsZscore,
                   Pattern      = src.Pattern,
                   ComputedAt   = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT ([Date], DayOfWeek, MeanResidual, MaxAbsZscore, Pattern)
        VALUES (src.[Date], src.DayOfWeek, src.MeanResidual, src.MaxAbsZscore, src.Pattern);
    """
)


def merge_day_profiles(
    engine,  # type: ignore[no-untyped-def]
    rows: list[DayProfileRow],
) -> int:
    """MERGE every row into `dbo.DayProfiles`. Idempotent on Date.

    Returns the count of rows visited (input length). The MERGE has no
    cheap "did we change anything" signal across all rows in one shot;
    callers that need a delta count should compare row-counts before
    and after. The slice-9 router returns this count as `rowsReplaced`
    in the response envelope (matches the contract's wire shape: it
    is "how many rows were operated on", not "how many were strictly
    new" — re-runs are idempotent so the cardinality is stable).
    """
    if not rows:
        return 0

    visited = 0
    with engine.begin() as conn:
        for row in rows:
            conn.execute(
                _MERGE_SQL,
                {
                    "date_value": row.date,
                    "day_of_week": int(row.day_of_week),
                    "mean_residual": float(row.mean_residual),
                    "max_abs_zscore": float(row.max_abs_zscore),
                },
            )
            visited += 1
    log.info("merge_day_profiles: visited=%d rows", visited)
    return visited


_RANGE_SQL = text(
    """
    SELECT DayProfileId, [Date], DayOfWeek, MeanResidual, MaxAbsZscore,
           Pattern, ComputedAt
      FROM dbo.fv_dayprofiles_at_cursor(:as_of)
     WHERE [Date] >= :start_date
       AND [Date] <= :end_date
     ORDER BY [Date] ASC;
    """
)


def read_day_profiles_at_cursor(
    engine,  # type: ignore[no-untyped-def]
    *,
    snap: CursorSnapshot,
    start_date: date,
    end_date: date,
) -> list[PersistedDayProfileRow]:
    """Return rows visible at the cursor for `[start_date, end_date]`.

    Reads through `dbo.fv_dayprofiles_at_cursor(@asOf)` (init-db.sql §3.3)
    — cursor-clipping is a property of the schema.
    """
    params = {
        "as_of": _to_naive_utc(snap.as_of),
        "start_date": start_date,
        "end_date": end_date,
    }
    with engine.connect() as conn:
        rows = conn.execute(_RANGE_SQL, params).fetchall()
    def _as_date(v) -> date:  # type: ignore[no-untyped-def]
        # datetime is a subclass of date — check it first.
        if isinstance(v, datetime):
            return v.date()
        if isinstance(v, date):
            return v
        raise TypeError(f"unexpected Date column value type: {type(v)!r}")

    return [
        PersistedDayProfileRow(
            day_profile_id=int(r[0]),
            date=_as_date(r[1]),
            day_of_week=int(r[2]),
            mean_residual=float(r[3]),
            max_abs_zscore=float(r[4]),
            pattern=str(r[5]),
            computed_at=_to_utc(r[6]),
        )
        for r in rows
    ]


def read_max_profile_date(
    engine,  # type: ignore[no-untyped-def]
) -> date | None:
    """Read the largest `Date` in `dbo.DayProfiles` (no cursor clip)."""
    with engine.connect() as conn:
        value = conn.execute(
            text("SELECT MAX([Date]) FROM dbo.DayProfiles")
        ).scalar()
    if value is None:
        return None
    # `datetime` is a subclass of `date` so the isinstance order matters
    # — check datetime first.
    if isinstance(value, datetime):
        return value.date()
    if isinstance(value, date):
        return value
    raise TypeError(f"unexpected MAX([Date]) value type: {type(value)!r}")


__all__ = [
    "PersistedDayProfileRow",
    "merge_day_profiles",
    "read_day_profiles_at_cursor",
    "read_max_profile_date",
]
