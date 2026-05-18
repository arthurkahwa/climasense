"""Tests for `ResidualOutlierDetector` — model-driven anomaly detection.

Three concerns:

  * The INSERT statement targets `dbo.Anomalies` with
    `AnomalyType='residual_outlier'`, gated by `WHERE NOT EXISTS`.
  * Constructor rejects an un-fitted forecaster (the detector relies
    on `predict()` so the boot-fit MUST have completed).
  * Behavioural: a synthetic series with a single >z outlier yields
    exactly one inserted row; the rest of the series falls under the
    z-threshold.

For the behavioural test we use a *stub* forecaster that returns a
constant prediction (so the test doesn't depend on sklearn's
coefficients) and a synthetic hourly history.
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone
from dataclasses import dataclass

import numpy as np
import pandas as pd
import pytest

from climasense_ml import anomaly_residual
from climasense_ml.anomaly_residual import (
    DEFAULT_Z_THRESHOLD,
    ResidualOutlierDetector,
    SCAN_WINDOW,
)
from climasense_ml.cursor import CursorSnapshot
from climasense_ml.forecaster import LAGS


# ---------------------------------------------------------------------
# SQL shape pinning
# ---------------------------------------------------------------------
def test_insert_sql_targets_anomalies_with_residual_outlier_type() -> None:
    sql = str(anomaly_residual._INSERT_SQL).upper()
    assert "INSERT INTO DBO.ANOMALIES" in sql
    assert "'RESIDUAL_OUTLIER'" in sql
    assert "WHERE NOT EXISTS" in sql
    assert "A.ANOMALYTYPE = 'RESIDUAL_OUTLIER'" in sql
    assert "A.READINGTIME = :READING_TIME" in sql


def test_module_constants_remain_pinned() -> None:
    assert SCAN_WINDOW == timedelta(hours=24)
    assert DEFAULT_Z_THRESHOLD == 3.0


# ---------------------------------------------------------------------
# Constructor guard — un-fitted forecaster is a hard error.
# ---------------------------------------------------------------------
@dataclass
class _StubFitSummary:
    n_train: int = 100
    n_test: int = 14
    n_features: int = 13
    fit_milliseconds: float = 1.0
    mae: float = 0.5
    rmse: float = 0.7
    temperature_residual_std: float = 1.0
    humidity_residual_std: float = 5.0


class _StubForecaster:
    """Minimal stand-in for `LagLinearForecaster`. Constant prediction."""

    def __init__(
        self,
        *,
        prediction: float = 22.0,
        fitted: bool = True,
        summary: _StubFitSummary | None = None,
    ) -> None:
        self._prediction = prediction
        self._fitted = fitted
        self.summary = summary or _StubFitSummary()

    @property
    def fitted(self) -> bool:
        return self._fitted

    def predict(
        self,
        history_tail: pd.DataFrame,
        horizon_hours: int,
        *,
        start_time: datetime | None = None,
    ) -> pd.DataFrame:
        target = start_time or (history_tail.index[-1] + pd.Timedelta(hours=1))
        index = pd.DatetimeIndex(
            [pd.Timestamp(target) for _ in range(horizon_hours)],
            name="target_time",
        )
        return pd.DataFrame(
            {
                "predicted_temperature": [self._prediction] * horizon_hours,
                "predicted_humidity": [50.0] * horizon_hours,
                "confidence_lower_temp": [self._prediction - 1.96] * horizon_hours,
                "confidence_upper_temp": [self._prediction + 1.96] * horizon_hours,
            },
            index=index,
        )


def test_constructor_rejects_unfitted_forecaster() -> None:
    forecaster = _StubForecaster(fitted=False)
    with pytest.raises(ValueError):
        ResidualOutlierDetector(
            engine=_StubEngine(),
            forecaster=forecaster,
        )


# ---------------------------------------------------------------------
# Behaviour — synthetic series with one large outlier.
# ---------------------------------------------------------------------
class _StubEngine:
    def __init__(self) -> None:
        self.inserts: list[dict] = []

    def begin(self) -> "_StubConn":
        return _StubConn(self)


class _StubConn:
    def __init__(self, engine: _StubEngine) -> None:
        self._engine = engine

    def __enter__(self) -> "_StubConn":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:  # noqa: ANN001
        del exc_type, exc, tb

    def execute(self, stmt, params=None):  # noqa: ANN001
        upper = str(stmt).strip().upper()
        params = dict(params or {})
        if "INSERT INTO DBO.ANOMALIES" in upper:
            # Idempotent on (anomaly_type, reading_time).
            already = any(
                i["reading_time"] == params["reading_time"]
                for i in self._engine.inserts
            )
            if already:
                return _StubResult(rowcount=0)
            self._engine.inserts.append(params)
            return _StubResult(rowcount=1)
        raise NotImplementedError(stmt)


class _StubResult:
    def __init__(self, *, rowcount: int = 0) -> None:
        self.rowcount = rowcount


def _hourly_series(
    *,
    end: datetime,
    n_hours: int,
    temperature: float = 22.0,
    spike_offset_hours: int | None = None,
    spike_value: float = 30.0,
) -> pd.DataFrame:
    """Build an hourly history ending at `end` with optional outlier."""
    index = pd.date_range(
        end=end, periods=n_hours, freq="h", tz="UTC"
    )
    temps = np.full(n_hours, temperature, dtype=float)
    if spike_offset_hours is not None:
        # Position of the spike (counting back from the cursor).
        spike_idx = n_hours - 1 - spike_offset_hours
        temps[spike_idx] = spike_value
    return pd.DataFrame(
        {
            "temperature": temps,
            "humidity": np.full(n_hours, 50.0),
        },
        index=index,
    )


def test_scan_recent_inserts_one_row_for_a_single_outlier() -> None:
    cursor = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    # 240 hours of history; the spike sits 6 hours before the cursor,
    # well inside the 24h scan window. Spike value is 30°C vs baseline
    # 22°C — residual ~8°C, σ from the stub summary is 1.0, so z=8 > 3.
    max_lag = max(LAGS)
    n_hours = max_lag * 2  # ample warm-up
    history = _hourly_series(
        end=cursor,
        n_hours=n_hours,
        temperature=22.0,
        spike_offset_hours=6,
        spike_value=30.0,
    )

    engine = _StubEngine()
    forecaster = _StubForecaster(prediction=22.0)

    detector = ResidualOutlierDetector(
        engine=engine,
        forecaster=forecaster,
        history_loader=lambda: history,
    )
    snap = CursorSnapshot(as_of=cursor)

    result = detector.scan_recent(snap)

    assert result.inserted == 1
    assert len(engine.inserts) == 1
    inserted = engine.inserts[0]
    assert inserted["reading_time"].replace(tzinfo=None) == (
        cursor - timedelta(hours=6)
    ).replace(tzinfo=None)
    assert inserted["severity"] > DEFAULT_Z_THRESHOLD


def test_scan_recent_is_idempotent_on_rerun() -> None:
    cursor = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    max_lag = max(LAGS)
    n_hours = max_lag * 2
    history = _hourly_series(
        end=cursor,
        n_hours=n_hours,
        temperature=22.0,
        spike_offset_hours=6,
        spike_value=30.0,
    )

    engine = _StubEngine()
    forecaster = _StubForecaster(prediction=22.0)

    detector = ResidualOutlierDetector(
        engine=engine,
        forecaster=forecaster,
        history_loader=lambda: history,
    )
    snap = CursorSnapshot(as_of=cursor)

    first = detector.scan_recent(snap)
    second = detector.scan_recent(snap)

    assert first.inserted == 1
    assert second.inserted == 0
    assert len(engine.inserts) == 1


def test_scan_recent_returns_zero_on_empty_history() -> None:
    engine = _StubEngine()
    forecaster = _StubForecaster()
    detector = ResidualOutlierDetector(
        engine=engine,
        forecaster=forecaster,
        history_loader=lambda: pd.DataFrame(),
    )
    snap = CursorSnapshot(as_of=datetime(2026, 5, 17, 12, 0, tzinfo=timezone.utc))

    result = detector.scan_recent(snap)

    assert result.inserted == 0
    assert engine.inserts == []
