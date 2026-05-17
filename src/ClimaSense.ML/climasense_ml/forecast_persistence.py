"""Persistence for the lag-LR forecast emissions.

Two operations:

  * `persist_forecast(engine, generated_at, points, model_version)` —
    bulk-INSERT one row per forecast point into `dbo.Forecasts`.
    `GeneratedAt = cursor at emission; TargetTime = forecast horizon;
    ModelVersion = artefact identifier (e.g. lag-lr-v1)`.

  * `read_latest_forecast_at_cursor(engine, as_of)` — read the
    most-recently-generated forecast batch visible at `as_of` via the
    inline TVF `dbo.fv_forecasts_at_cursor(@asOf)`. The TVF is part of
    `init-db.sql`; reading through it makes cursor-clipping a property
    of the schema.

The forecast table allows multiple emissions to coexist (each carries
its own `GeneratedAt`). "Latest" picks the largest `GeneratedAt` group.
"""

from __future__ import annotations

import logging
from collections.abc import Iterable
from dataclasses import dataclass
from datetime import datetime, timezone

import pandas as pd
from sqlalchemy import text

log = logging.getLogger("climasense_ml.forecast_persistence")


@dataclass(frozen=True)
class PersistedForecastRow:
    forecast_id: int
    generated_at: datetime
    target_time: datetime
    predicted_temperature: float
    predicted_humidity: float
    confidence_lower_temp: float | None
    confidence_upper_temp: float | None
    model_version: str


_INSERT_SQL = text(
    """
    INSERT INTO dbo.Forecasts
        (GeneratedAt, TargetTime, PredictedTemperature, PredictedHumidity,
         ConfidenceLowerTemp, ConfidenceUpperTemp, ModelVersion)
    OUTPUT INSERTED.ForecastId, INSERTED.GeneratedAt, INSERTED.TargetTime,
           INSERTED.PredictedTemperature, INSERTED.PredictedHumidity,
           INSERTED.ConfidenceLowerTemp, INSERTED.ConfidenceUpperTemp,
           INSERTED.ModelVersion
    VALUES (:generated_at, :target_time, :predicted_temperature, :predicted_humidity,
            :confidence_lower_temp, :confidence_upper_temp, :model_version)
    """
)


def _to_utc(value: datetime) -> datetime:
    """Coerce to a UTC-aware datetime."""
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


def persist_forecast(
    engine,  # type: ignore[no-untyped-def]
    *,
    generated_at: datetime,
    points: pd.DataFrame,
    model_version: str,
) -> list[PersistedForecastRow]:
    """Bulk-insert forecast rows. Returns the persisted rows with IDs.

    `points` must be indexed by `target_time` (UTC) and have columns
    `predicted_temperature`, `predicted_humidity`,
    `confidence_lower_temp`, `confidence_upper_temp`.

    Each row is inserted in its own statement so we can OUTPUT the
    IDENTITY column (`ForecastId`) in the response envelope. The table
    is append-only; multiple emissions at the same `GeneratedAt` are
    allowed by the schema (cursor-clipping happens at read time via
    `dbo.fv_forecasts_at_cursor`).
    """
    if points.empty:
        return []

    generated_at_utc = _to_utc(generated_at)
    inserted: list[PersistedForecastRow] = []

    with engine.begin() as conn:
        for target_time, row in points.iterrows():
            target_utc = _to_utc(pd.Timestamp(target_time).to_pydatetime())
            params = {
                "generated_at": generated_at_utc.replace(tzinfo=None),
                "target_time": target_utc.replace(tzinfo=None),
                "predicted_temperature": float(row["predicted_temperature"]),
                "predicted_humidity": float(row["predicted_humidity"]),
                "confidence_lower_temp": (
                    float(row["confidence_lower_temp"])
                    if "confidence_lower_temp" in row and pd.notna(row["confidence_lower_temp"])
                    else None
                ),
                "confidence_upper_temp": (
                    float(row["confidence_upper_temp"])
                    if "confidence_upper_temp" in row and pd.notna(row["confidence_upper_temp"])
                    else None
                ),
                "model_version": model_version,
            }
            result = conn.execute(_INSERT_SQL, params)
            inserted_row = result.fetchone()
            if inserted_row is None:
                continue
            inserted.append(
                PersistedForecastRow(
                    forecast_id=int(inserted_row[0]),
                    generated_at=_to_utc(inserted_row[1]),
                    target_time=_to_utc(inserted_row[2]),
                    predicted_temperature=float(inserted_row[3]),
                    predicted_humidity=float(inserted_row[4]),
                    confidence_lower_temp=(
                        float(inserted_row[5]) if inserted_row[5] is not None else None
                    ),
                    confidence_upper_temp=(
                        float(inserted_row[6]) if inserted_row[6] is not None else None
                    ),
                    model_version=str(inserted_row[7]),
                )
            )

    log.info(
        "persist_forecast: inserted %d rows generated_at=%s model_version=%s",
        len(inserted),
        generated_at_utc.isoformat(),
        model_version,
    )
    return inserted


_LATEST_BATCH_SQL = text(
    """
    DECLARE @latest DATETIME2(3) =
        (SELECT MAX(GeneratedAt) FROM dbo.fv_forecasts_at_cursor(:as_of));
    SELECT ForecastId, GeneratedAt, TargetTime,
           PredictedTemperature, PredictedHumidity,
           ConfidenceLowerTemp, ConfidenceUpperTemp, ModelVersion
      FROM dbo.fv_forecasts_at_cursor(:as_of)
     WHERE GeneratedAt = @latest
     ORDER BY TargetTime ASC;
    """
)


def read_latest_forecast_at_cursor(
    engine,  # type: ignore[no-untyped-def]
    *,
    as_of: datetime,
) -> list[PersistedForecastRow]:
    """Read the most recent forecast batch visible at `as_of`.

    Goes through `dbo.fv_forecasts_at_cursor(@asOf)` (the inline TVF
    defined in `init-db.sql`) so cursor-clipping is a property of the
    schema, not of caller discipline. Returns rows ordered by
    `TargetTime ASC`. Empty list when no forecast has been emitted at
    or before `as_of`.
    """
    as_of_utc = _to_utc(as_of)
    with engine.connect() as conn:
        rows = conn.execute(_LATEST_BATCH_SQL, {"as_of": as_of_utc.replace(tzinfo=None)}).fetchall()
    return [
        PersistedForecastRow(
            forecast_id=int(r[0]),
            generated_at=_to_utc(r[1]),
            target_time=_to_utc(r[2]),
            predicted_temperature=float(r[3]),
            predicted_humidity=float(r[4]),
            confidence_lower_temp=(float(r[5]) if r[5] is not None else None),
            confidence_upper_temp=(float(r[6]) if r[6] is not None else None),
            model_version=str(r[7]),
        )
        for r in rows
    ]


def read_max_generated_at(
    engine,  # type: ignore[no-untyped-def]
) -> datetime | None:
    """Read the largest `GeneratedAt` in `dbo.Forecasts` (no cursor clip).

    Used by the APScheduler emission job to drive its β-prime gate.
    """
    with engine.connect() as conn:
        value = conn.execute(text("SELECT MAX(GeneratedAt) FROM dbo.Forecasts")).scalar()
    if value is None:
        return None
    return _to_utc(value)


__all__ = [
    "PersistedForecastRow",
    "persist_forecast",
    "read_latest_forecast_at_cursor",
    "read_max_generated_at",
]
