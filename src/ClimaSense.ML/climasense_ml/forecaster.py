"""LagLinearForecaster — slice 5's production forecaster.

Replicates the notebook §8.3 setup exactly so the boot-fit coefficients
are deterministic and the live-tier MAE/RMSE on the held-out 14-day
window match `assets/results.json` row "Linear regression (lags)" within
1e-6 absolute tolerance.

Per ADR-0011 (interface-emergence policy): this is a CONCRETE class.
There is no Python `Protocol` seam over the forecaster — the seam
emerges from two concrete forecasters at the moment the second
arrives. As of slice 5 only this class exists, so a grep for a
speculative interface returns zero matches by design (the rule is
locked by ``test_no_iforecaster_protocol_in_codebase`` in the golden
test).

Per the epic ML-and-analytics section: `predict()` only after startup —
no `fit()` at request time. The constructor performs a one-shot
deterministic re-fit, then exposes coefficients for the lifetime of the
process.

The temperature target carries the full notebook recipe (which is the
golden-test surface). The humidity target uses the *same* feature
matrix and a sibling `LinearRegression`. The notebook never validated
humidity numerically; the forecaster persists it because the
`Forecasts` schema demands it.
"""

from __future__ import annotations

import logging
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import numpy as np
import pandas as pd
from sklearn.linear_model import LinearRegression
from sklearn.metrics import mean_absolute_error, mean_squared_error

log = logging.getLogger("climasense_ml.forecaster")


# ---------------------------------------------------------------------
# Hyperparameters (notebook §8.2 + §8.3).
# ---------------------------------------------------------------------
LAGS: tuple[int, ...] = (1, 2, 3, 6, 12, 24, 48, 168)
"""Lag offsets in hours. Notebook cell 87 (build_features default)."""

TEST_HOURS: int = 24 * 14
"""Held-out test window length. Notebook cell 60 (`horizon_test = 24 * 14`)."""

MODEL_VERSION: str = "lag-lr-v1"
"""Identifier persisted to `Forecasts.ModelVersion` on every row."""

# 95 % CI multiplier for a two-sided normal distribution. The notebook
# does not compute CI bands for the lag-LR row; we derive them from the
# in-sample residual standard deviation as `±1.96 · σ_residuals`.
_CI_Z: float = 1.959964


# ---------------------------------------------------------------------
# Fit summary — what the lifespan logs.
# ---------------------------------------------------------------------
@dataclass(frozen=True)
class FitSummary:
    """Outcome of `LagLinearForecaster.fit_at_startup()`.

    `mae` / `rmse` are computed on the same held-out 14-day window the
    notebook uses, so they are directly comparable to
    `assets/results.json::sequence_results` row 0 ("Linear regression (lags)").

    `temperature_residual_std` and `humidity_residual_std` are the
    in-sample residual standard deviations used to derive the 95 % CI
    bands on `predict()`.
    """

    n_train: int
    n_test: int
    n_features: int
    fit_milliseconds: float
    mae: float
    rmse: float
    temperature_residual_std: float
    humidity_residual_std: float


# ---------------------------------------------------------------------
# Feature engineering — public so the golden test can compare emission
# row-for-row against an independent build.
# ---------------------------------------------------------------------
def build_features(
    temperature: pd.Series,
    *,
    lags: tuple[int, ...] = LAGS,
) -> tuple[pd.DataFrame, pd.Series]:
    """Replicate the notebook's `build_features` (cell 87) exactly.

    Returns `(X, y)` aligned on the same index. `X` has columns:

        lag_1, lag_2, lag_3, lag_6, lag_12, lag_24, lag_48, lag_168,
        hour_sin, hour_cos, dow_sin, dow_cos, month

    `y` is the unshifted target. Warmup rows where any lag is undefined
    are dropped, matching the notebook's `frame.dropna()` step.
    """
    frame = pd.DataFrame({"y": temperature.astype(float)})
    for lag in lags:
        frame[f"lag_{lag}"] = frame["y"].shift(lag)

    idx = frame.index
    hour = idx.hour.to_numpy()
    dow = idx.dayofweek.to_numpy()
    frame["hour_sin"] = np.sin(2 * np.pi * hour / 24)
    frame["hour_cos"] = np.cos(2 * np.pi * hour / 24)
    frame["dow_sin"] = np.sin(2 * np.pi * dow / 7)
    frame["dow_cos"] = np.cos(2 * np.pi * dow / 7)
    frame["month"] = idx.month.to_numpy()

    frame = frame.dropna()
    y = frame["y"]
    x = frame.drop(columns=["y"])
    return x, y


def _calendar_features_for(ts: pd.Timestamp) -> dict[str, float]:
    """Calendar features for a single timestamp; called from `predict`."""
    return {
        "hour_sin": float(np.sin(2 * np.pi * ts.hour / 24)),
        "hour_cos": float(np.cos(2 * np.pi * ts.hour / 24)),
        "dow_sin": float(np.sin(2 * np.pi * ts.dayofweek / 7)),
        "dow_cos": float(np.cos(2 * np.pi * ts.dayofweek / 7)),
        "month": float(ts.month),
    }


# ---------------------------------------------------------------------
# LagLinearForecaster — concrete class. No Protocol seam (ADR-0011).
# ---------------------------------------------------------------------
class LagLinearForecaster:
    """Boot-fit lag-LR forecaster for temperature + humidity.

    Lifecycle:
        1. Construct.
        2. Call `fit_at_startup(history)` once during FastAPI lifespan.
        3. Call `predict(history_tail, horizon_hours)` per request.
    """

    def __init__(self) -> None:
        # `None` flags un-fitted; both regressors are populated in lockstep
        # by `fit_at_startup` so they're checked together at predict time.
        self._temperature_model: LinearRegression | None = None
        self._humidity_model: LinearRegression | None = None
        self._feature_names: tuple[str, ...] = ()
        self._summary: FitSummary | None = None

    # -----------------------------------------------------------------
    @property
    def fitted(self) -> bool:
        return self._temperature_model is not None

    @property
    def summary(self) -> FitSummary | None:
        return self._summary

    @property
    def model_version(self) -> str:
        return MODEL_VERSION

    # -----------------------------------------------------------------
    def fit_at_startup(self, history: pd.DataFrame) -> FitSummary:
        """Re-fit the lag-LR coefficients deterministically.

        `history` must be a hourly DataFrame indexed by UTC datetime
        with columns `temperature` and `humidity`. The caller is
        responsible for the upstream pipeline (CSV / DB → hourly
        resample → linear interpolation).

        The fit holds out the final `TEST_HOURS` (= 336) rows for the
        leaderboard-comparable MAE / RMSE. Coefficients are then re-fit
        on the FULL series so `predict()` uses the maximum possible
        information.

        Idempotent — repeat calls yield identical coefficients.
        """
        if not isinstance(history, pd.DataFrame):
            raise TypeError("history must be a pandas DataFrame")
        for col in ("temperature", "humidity"):
            if col not in history.columns:
                raise ValueError(f"history is missing required column {col!r}")
        if len(history) < TEST_HOURS + max(LAGS) + 1:
            raise ValueError(
                f"history has {len(history)} hourly rows; need at least "
                f"{TEST_HOURS + max(LAGS) + 1} for a sensible fit"
            )

        t_start = time.perf_counter()

        # Stage 1: holdout split → measurement of MAE/RMSE against the
        # notebook's 14-day test window. This is the golden-test surface.
        temperature = history["temperature"].astype(float).dropna()
        train_series = temperature.iloc[:-TEST_HOURS]
        test_series = temperature.iloc[-TEST_HOURS:]

        x_all, y_all = build_features(temperature)
        x_train = x_all.loc[x_all.index.intersection(train_series.index)]
        y_train = y_all.loc[x_train.index]
        x_test = x_all.loc[x_all.index.intersection(test_series.index)]
        y_test = y_all.loc[x_test.index]

        holdout_model = LinearRegression()
        holdout_model.fit(x_train.to_numpy(), y_train.to_numpy())
        y_pred = holdout_model.predict(x_test.to_numpy())
        mae = float(mean_absolute_error(y_test.to_numpy(), y_pred))
        rmse = float(np.sqrt(mean_squared_error(y_test.to_numpy(), y_pred)))

        # Stage 2: final production fit on the FULL series so request-
        # time `predict()` uses every available row. The held-out
        # numbers above already characterise generalisation.
        temperature_model = LinearRegression()
        temperature_model.fit(x_all.to_numpy(), y_all.to_numpy())

        humidity = history["humidity"].astype(float).dropna()
        # Reuse the feature builder for humidity: same lag/calendar
        # structure on the humidity series.
        x_h_all, y_h_all = build_features(humidity)
        humidity_model = LinearRegression()
        humidity_model.fit(x_h_all.to_numpy(), y_h_all.to_numpy())

        # Residual standard deviations for the 95 % CI bands.
        temperature_residuals = y_all.to_numpy() - temperature_model.predict(x_all.to_numpy())
        humidity_residuals = y_h_all.to_numpy() - humidity_model.predict(x_h_all.to_numpy())
        temperature_residual_std = float(np.std(temperature_residuals, ddof=1))
        humidity_residual_std = float(np.std(humidity_residuals, ddof=1))

        self._temperature_model = temperature_model
        self._humidity_model = humidity_model
        self._feature_names = tuple(x_all.columns)
        fit_ms = (time.perf_counter() - t_start) * 1000.0

        summary = FitSummary(
            n_train=int(len(x_train)),
            n_test=int(len(x_test)),
            n_features=len(self._feature_names),
            fit_milliseconds=fit_ms,
            mae=mae,
            rmse=rmse,
            temperature_residual_std=temperature_residual_std,
            humidity_residual_std=humidity_residual_std,
        )
        self._summary = summary

        log.info(
            "LagLinearForecaster: fit complete "
            "(n_train=%d, n_test=%d, features=%d, MAE=%.4f, RMSE=%.4f, %.0f ms)",
            summary.n_train,
            summary.n_test,
            summary.n_features,
            summary.mae,
            summary.rmse,
            summary.fit_milliseconds,
        )
        return summary

    # -----------------------------------------------------------------
    def predict(
        self,
        history_tail: pd.DataFrame,
        horizon_hours: int,
        *,
        start_time: datetime | None = None,
    ) -> pd.DataFrame:
        """Recursively forecast `horizon_hours` steps ahead.

        `history_tail` must be a DataFrame with `temperature` and
        `humidity` columns indexed by UTC hourly timestamps. The tail
        must cover at least `max(LAGS) = 168` hours so every lag is
        available for the first prediction step.

        Each prediction step uses the most recent `max(LAGS)` values
        for both targets (true values where available, predicted values
        otherwise — i.e. recursive multi-step).

        `start_time` defaults to `history_tail.index[-1] + 1 hour`.

        Returns a DataFrame indexed by `target_time` (UTC) with columns:
            predicted_temperature, predicted_humidity,
            confidence_lower_temp, confidence_upper_temp.
        """
        if not self.fitted or self._temperature_model is None or self._humidity_model is None:
            raise RuntimeError(
                "LagLinearForecaster.predict called before fit_at_startup. "
                "The forecaster must be boot-fitted before serving traffic."
            )
        if not (1 <= horizon_hours <= 168):
            raise ValueError("horizon_hours must be in [1, 168]")
        if len(history_tail) < max(LAGS):
            raise ValueError(
                f"history_tail has {len(history_tail)} rows; need at least "
                f"{max(LAGS)} so every lag is defined."
            )

        # Working buffers; tail-most value is at index -1.
        t_buf = list(history_tail["temperature"].astype(float).to_numpy())
        h_buf = list(history_tail["humidity"].astype(float).to_numpy())

        last_known = history_tail.index[-1]
        if not isinstance(last_known, pd.Timestamp):
            last_known = pd.Timestamp(last_known)
        if last_known.tzinfo is None:
            last_known = last_known.tz_localize("UTC")
        else:
            last_known = last_known.tz_convert("UTC")

        first_target = (
            pd.Timestamp(start_time).tz_convert("UTC")
            if (start_time is not None and pd.Timestamp(start_time).tzinfo is not None)
            else (
                pd.Timestamp(start_time).tz_localize("UTC")
                if start_time is not None
                else last_known + pd.Timedelta(hours=1)
            )
        )

        residual_std_t = (
            self._summary.temperature_residual_std if self._summary is not None else 0.0
        )

        rows: list[dict[str, object]] = []
        for step in range(horizon_hours):
            target = first_target + pd.Timedelta(hours=step)
            calendar = _calendar_features_for(target)
            row_t = [t_buf[-lag] for lag in LAGS] + [
                calendar["hour_sin"],
                calendar["hour_cos"],
                calendar["dow_sin"],
                calendar["dow_cos"],
                calendar["month"],
            ]
            row_h = [h_buf[-lag] for lag in LAGS] + [
                calendar["hour_sin"],
                calendar["hour_cos"],
                calendar["dow_sin"],
                calendar["dow_cos"],
                calendar["month"],
            ]
            x_row_t = np.asarray(row_t, dtype=float).reshape(1, -1)
            x_row_h = np.asarray(row_h, dtype=float).reshape(1, -1)
            y_hat_t = float(self._temperature_model.predict(x_row_t)[0])
            y_hat_h = float(self._humidity_model.predict(x_row_h)[0])

            t_buf.append(y_hat_t)
            h_buf.append(y_hat_h)

            rows.append(
                {
                    "target_time": target.to_pydatetime().replace(tzinfo=timezone.utc),
                    "predicted_temperature": y_hat_t,
                    "predicted_humidity": y_hat_h,
                    "confidence_lower_temp": y_hat_t - _CI_Z * residual_std_t,
                    "confidence_upper_temp": y_hat_t + _CI_Z * residual_std_t,
                }
            )

        return pd.DataFrame(rows).set_index("target_time")

    # -----------------------------------------------------------------
    def evaluate_on_holdout(
        self, history: pd.DataFrame
    ) -> tuple[float, float]:
        """Return `(MAE, RMSE)` on the same held-out 14-day window the
        notebook uses, recomputed from the held-out fit.

        Public so the golden test can assert without going through the
        log-line parser.
        """
        temperature = history["temperature"].astype(float).dropna()
        train_series = temperature.iloc[:-TEST_HOURS]
        test_series = temperature.iloc[-TEST_HOURS:]
        x_all, y_all = build_features(temperature)
        x_train = x_all.loc[x_all.index.intersection(train_series.index)]
        y_train = y_all.loc[x_train.index]
        x_test = x_all.loc[x_all.index.intersection(test_series.index)]
        y_test = y_all.loc[x_test.index]
        model = LinearRegression()
        model.fit(x_train.to_numpy(), y_train.to_numpy())
        y_pred = model.predict(x_test.to_numpy())
        mae = float(mean_absolute_error(y_test.to_numpy(), y_pred))
        rmse = float(np.sqrt(mean_squared_error(y_test.to_numpy(), y_pred)))
        return mae, rmse


__all__ = [
    "LAGS",
    "TEST_HOURS",
    "MODEL_VERSION",
    "FitSummary",
    "build_features",
    "LagLinearForecaster",
]


def load_hourly_from_csv(csv_path: str) -> pd.DataFrame:
    """Helper for tests and lifespan fallback — re-derive the hourly
    series from the notebook's CSV using the exact pipeline from cells
    6 and 8.

    Returns a DataFrame indexed by UTC hourly timestamps with columns
    `temperature` and `humidity`.
    """
    raw = pd.read_csv(csv_path)
    df = (
        raw.sort_values("sensor_dateTime")
        .drop_duplicates("sensor_dateTime", keep="first")
        .set_index("sensor_dateTime")
    )
    if "id" in df.columns:
        df = df.drop(columns=["id"])
    df.index = pd.to_datetime(df.index)
    df.index.name = "timestamp"
    # Pandas 2.x: 'H'; Pandas 3.x: 'h'. Try the lower-case spelling first.
    try:
        df_h = df.resample("h").mean().interpolate(method="time", limit_direction="both")
    except ValueError:
        df_h = df.resample("H").mean().interpolate(method="time", limit_direction="both")
    # Localise to UTC so downstream consumers don't see naive datetimes.
    if df_h.index.tz is None:
        df_h.index = df_h.index.tz_localize("UTC")
    return df_h


def load_hourly_from_sql(engine) -> pd.DataFrame:  # type: ignore[no-untyped-def]
    """Read `dbo.SensorReadings` into a pandas DataFrame and project to
    the same hourly grid the notebook uses.

    The bootstrap pipeline (`IngestionService.bootstrap_from_csv_if_empty`)
    populates `SensorReadings` from `sensor_data.csv` minus duplicates,
    so this query yields the same row population the notebook started
    with. The hourly resample + linear interpolation then matches cell
    8 by construction.
    """
    # Local import so `forecaster` stays importable without sqlalchemy
    # in test contexts that don't touch the DB.
    from sqlalchemy import text

    query = text(
        "SELECT ReadingTime, "
        "       CAST(Temperature AS FLOAT) AS Temperature, "
        "       CAST(Humidity    AS FLOAT) AS Humidity "
        "  FROM dbo.SensorReadings "
        " ORDER BY ReadingTime"
    )
    with engine.connect() as conn:
        df = pd.read_sql(query, conn, parse_dates=["ReadingTime"])
    df = df.rename(
        columns={
            "ReadingTime": "timestamp",
            "Temperature": "temperature",
            "Humidity": "humidity",
        }
    )
    df = df.set_index("timestamp")
    try:
        df_h = df.resample("h").mean().interpolate(method="time", limit_direction="both")
    except ValueError:
        df_h = df.resample("H").mean().interpolate(method="time", limit_direction="both")
    if df_h.index.tz is None:
        df_h.index = df_h.index.tz_localize("UTC")
    return df_h
