"""Persistence for comfort scores (slice 7).

Three operations:

  * `upsert_comfort_score(engine, bucket_time, result)` — MERGE one row
    into `dbo.ComfortScores`. Idempotent on `(BucketTime)` — re-running
    at the same bucket replaces the row with the same values produced
    by the pure scorer.
  * `read_latest_comfort_at_cursor(engine, as_of)` — read the
    most-recent `ComfortScores` row visible at `as_of` via the inline
    TVF `dbo.fv_comfortscores_at_cursor(@asOf)`. The TVF is part of
    `init-db.sql §3.4`; reading through it makes cursor-clipping a
    property of the schema.
  * `read_recent_comfort_at_cursor(engine, as_of, hours)` — read the
    trailing `hours` of comfort rows visible at `as_of`. Used by the
    `GET /api/comfort/score` endpoint to surface a window.
  * `read_max_bucket_time(engine)` — read the max `BucketTime` for
    the β-prime emission gate.

Schema (init-db.sql §2.5):

    CREATE TABLE dbo.ComfortScores (
        ComfortScoreId BIGINT IDENTITY(1,1) NOT NULL,
        BucketTime     DATETIME2(3) NOT NULL,
        Score          DECIMAL(5, 2) NOT NULL,
        Rating         VARCHAR(16) NOT NULL,
        Season         VARCHAR(8) NOT NULL,
        ComputedAt     DATETIME2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_ComfortScores_Bucket UNIQUE (BucketTime),
        CONSTRAINT CK_ComfortScores_Rating CHECK
            (Rating IN ('excellent', 'acceptable', 'marginal', 'uncomfortable')),
        CONSTRAINT CK_ComfortScores_Season CHECK (Season IN ('summer', 'winter')),
        CONSTRAINT CK_ComfortScores_Score CHECK (Score BETWEEN 0 AND 100)
    );
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

from sqlalchemy import text

from .comfort import ComfortScoreResult

log = logging.getLogger("climasense_ml.comfort_persistence")


@dataclass(frozen=True)
class PersistedComfortRow:
    comfort_score_id: int
    bucket_time: datetime
    score: float
    rating: str
    season: str
    computed_at: datetime


def _to_utc(value: datetime) -> datetime:
    """Coerce to a UTC-aware datetime."""
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


_MERGE_SQL = text(
    """
    MERGE dbo.ComfortScores AS target
    USING (SELECT
              :bucket_time AS BucketTime,
              :score       AS Score,
              :rating      AS Rating,
              :season      AS Season) AS src
       ON target.BucketTime = src.BucketTime
    WHEN MATCHED THEN
        UPDATE SET Score = src.Score,
                   Rating = src.Rating,
                   Season = src.Season,
                   ComputedAt = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (BucketTime, Score, Rating, Season)
        VALUES (src.BucketTime, src.Score, src.Rating, src.Season);
    """
)


def upsert_comfort_score(
    engine,  # type: ignore[no-untyped-def]
    *,
    bucket_time: datetime,
    result: ComfortScoreResult,
) -> None:
    """MERGE one comfort score row keyed on `BucketTime`.

    Idempotent: re-running on the same `BucketTime` updates the row in
    place rather than inserting a duplicate. The unique constraint on
    `BucketTime` enforces this at the schema level too.
    """
    bucket_utc = _to_utc(bucket_time)
    params = {
        "bucket_time": bucket_utc.replace(tzinfo=None),
        "score": float(result.score),
        "rating": result.rating,
        "season": result.season,
    }
    with engine.begin() as conn:
        conn.execute(_MERGE_SQL, params)
    log.info(
        "upsert_comfort_score: bucket_time=%s score=%.2f rating=%s season=%s",
        bucket_utc.isoformat(),
        result.score,
        result.rating,
        result.season,
    )


_LATEST_SQL = text(
    """
    SELECT TOP 1 ComfortScoreId, BucketTime, Score, Rating, Season, ComputedAt
      FROM dbo.fv_comfortscores_at_cursor(:as_of)
     ORDER BY BucketTime DESC;
    """
)


def read_latest_comfort_at_cursor(
    engine,  # type: ignore[no-untyped-def]
    *,
    as_of: datetime,
) -> PersistedComfortRow | None:
    """Return the most-recent `ComfortScores` row visible at `as_of`,
    or `None` if no rows have been emitted at or before the cursor.

    Goes through `dbo.fv_comfortscores_at_cursor(@asOf)` (the inline
    TVF defined in `init-db.sql §3.4`) so cursor-clipping is a property
    of the schema, not of caller discipline.
    """
    as_of_utc = _to_utc(as_of)
    with engine.connect() as conn:
        row = conn.execute(
            _LATEST_SQL, {"as_of": as_of_utc.replace(tzinfo=None)}
        ).fetchone()
    if row is None:
        return None
    return PersistedComfortRow(
        comfort_score_id=int(row[0]),
        bucket_time=_to_utc(row[1]),
        score=float(row[2]),
        rating=str(row[3]),
        season=str(row[4]),
        computed_at=_to_utc(row[5]),
    )


_RECENT_SQL = text(
    """
    SELECT ComfortScoreId, BucketTime, Score, Rating, Season, ComputedAt
      FROM dbo.fv_comfortscores_at_cursor(:as_of)
     WHERE BucketTime > :start
     ORDER BY BucketTime ASC;
    """
)


def read_recent_comfort_at_cursor(
    engine,  # type: ignore[no-untyped-def]
    *,
    as_of: datetime,
    hours: int,
) -> list[PersistedComfortRow]:
    """Return the trailing `hours` of `ComfortScores` visible at the cursor."""
    if hours <= 0:
        raise ValueError("hours must be strictly positive")
    as_of_utc = _to_utc(as_of)
    start = as_of_utc - timedelta(hours=hours)
    with engine.connect() as conn:
        rows = conn.execute(
            _RECENT_SQL,
            {
                "as_of": as_of_utc.replace(tzinfo=None),
                "start": start.replace(tzinfo=None),
            },
        ).fetchall()
    return [
        PersistedComfortRow(
            comfort_score_id=int(r[0]),
            bucket_time=_to_utc(r[1]),
            score=float(r[2]),
            rating=str(r[3]),
            season=str(r[4]),
            computed_at=_to_utc(r[5]),
        )
        for r in rows
    ]


def read_max_bucket_time(
    engine,  # type: ignore[no-untyped-def]
) -> datetime | None:
    """Read the largest `BucketTime` in `dbo.ComfortScores` (no cursor clip).

    Used by the APScheduler emission job to drive its β-prime gate.
    """
    with engine.connect() as conn:
        value = conn.execute(
            text("SELECT MAX(BucketTime) FROM dbo.ComfortScores")
        ).scalar()
    if value is None:
        return None
    return _to_utc(value)


__all__ = [
    "PersistedComfortRow",
    "upsert_comfort_score",
    "read_latest_comfort_at_cursor",
    "read_recent_comfort_at_cursor",
    "read_max_bucket_time",
]
