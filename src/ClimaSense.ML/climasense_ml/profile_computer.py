"""ProfileComputer — calendar-conditioned per-day residual aggregates (slice 9).

Pure-Python compute. No SQL. No I/O. The output rows are persisted by
`profile_persistence.merge_day_profiles` and labelled at SQL time via
the empirical `dbo.fn_classify_pattern` CASE function (init-db.sql §4).

Method (locked by `test_profile_computer.py`):

  1. Take a hourly `(temperature, humidity)` DataFrame indexed by UTC.
  2. Re-fit the lag-LR coefficients on the FULL series (same code path
     as `LagLinearForecaster.fit_at_startup` Stage 2 — no recursive
     multi-step here; this is a one-shot in-sample residual scan).
  3. Compute `residual_t = y_t - y_hat_t`.
  4. Group residuals by `(day_of_week, hour_of_day)` cohort. Compute
     cohort mean μ and std σ. Z-score: `z_t = (e_t - μ) / σ`.
  5. Aggregate to one row per calendar date:
        * MeanResidual = mean(e_t over day)
        * MaxAbsZscore = max(|z_t| over day)
        * DayOfWeek    = the day's `pandas.Timestamp.dayofweek` (0=Mon).

Per ADR-0011: concrete class, no `IProfileComputer` interface. A second
implementation (e.g. a streaming version) would extract the seam.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import date

import numpy as np
import pandas as pd
from sklearn.linear_model import LinearRegression

from .forecaster import LAGS, build_features

log = logging.getLogger("climasense_ml.profile_computer")


@dataclass(frozen=True)
class DayProfileRow:
    """One per-day aggregate. Lines up with `dbo.DayProfiles` columns."""

    date: date
    day_of_week: int
    mean_residual: float
    max_abs_zscore: float


class ProfileComputer:
    """Pure compute of `DayProfileRow`s from an hourly history.

    Construction has no side effects. Repeat calls with the same input
    produce identical output (sklearn's `LinearRegression` is OLS — a
    closed-form solver — and pandas groupby is deterministic on a
    sorted index).
    """

    @staticmethod
    def compute(
        history: pd.DataFrame,
        *,
        target_dates: list[date] | None = None,
    ) -> list[DayProfileRow]:
        """Return one `DayProfileRow` per calendar date covered by
        `history`.

        Parameters
        ----------
        history :
            Hourly DataFrame indexed by UTC datetime with a
            `temperature` column. The forecaster's exact pipeline
            (`load_hourly_from_sql` / `load_hourly_from_csv`)
            produces this shape.
        target_dates :
            When provided, restrict the output to these dates. The
            full residual + cohort computation still runs over the
            FULL history (so cohort μ/σ have maximum information),
            but only the requested dates are returned. Used by the
            APScheduler emitter to recompute "the last 7 replay-days"
            without re-emitting every historical day.

        Notes
        -----
        Empty cohorts (a `(dow, hour)` pair with fewer than two
        samples) have a degenerate σ. We clamp σ to a small positive
        epsilon (1e-9) so the z-score is well-defined; in practice a
        full year of hourly data has 52 samples per cohort so the
        guard is defensive, not load-bearing.
        """
        if not isinstance(history, pd.DataFrame):
            raise TypeError("history must be a pandas DataFrame")
        if "temperature" not in history.columns:
            raise ValueError("history is missing required column 'temperature'")
        if history.empty:
            return []

        temperature = history["temperature"].astype(float).dropna()
        if temperature.empty:
            return []

        # Stage 1 — fit + in-sample residuals. The full-series fit
        # matches `forecaster.fit_at_startup` Stage 2 ("production
        # fit"). This is OLS so the coefficients are deterministic.
        x_all, y_all = build_features(temperature)
        if x_all.empty:
            log.info(
                "ProfileComputer.compute: no rows survived the lag-warmup "
                "(history has %d rows; LAGS max=%d)",
                len(temperature),
                max(LAGS),
            )
            return []
        model = LinearRegression()
        model.fit(x_all.to_numpy(), y_all.to_numpy())
        y_hat = model.predict(x_all.to_numpy())
        residuals = pd.Series(
            y_all.to_numpy() - y_hat, index=y_all.index, name="residual"
        )

        # Stage 2 — calendar-cohort z-scores. The cohort granularity
        # is `(day_of_week, hour_of_day)`; per-cohort μ/σ are
        # broadcast back via `transform`.
        cohort = pd.DataFrame(
            {
                "residual": residuals.to_numpy(),
                "day_of_week": residuals.index.dayofweek.to_numpy(),
                "hour_of_day": residuals.index.hour.to_numpy(),
                "date": [ts.date() for ts in residuals.index],
            },
            index=residuals.index,
        )
        grouped = cohort.groupby(
            ["day_of_week", "hour_of_day"], sort=False
        )["residual"]
        cohort["mu"] = grouped.transform("mean")
        cohort["sigma"] = grouped.transform(lambda s: s.std(ddof=1))
        cohort["sigma"] = cohort["sigma"].fillna(0.0).clip(lower=1e-9)
        cohort["z"] = (cohort["residual"] - cohort["mu"]) / cohort["sigma"]

        # Stage 3 — per-day aggregation. `date` is the calendar-day
        # bucket; `dayofweek` is constant per `date` so we take its
        # first observed value.
        daily = cohort.groupby("date").agg(
            mean_residual=("residual", "mean"),
            max_abs_zscore=("z", lambda s: float(np.abs(s).max())),
            day_of_week=("day_of_week", "first"),
        )

        if target_dates is not None:
            keep = set(target_dates)
            daily = daily.loc[daily.index.isin(keep)]

        rows: list[DayProfileRow] = []
        for d, row in daily.iterrows():
            rows.append(
                DayProfileRow(
                    date=d,
                    day_of_week=int(row["day_of_week"]),
                    mean_residual=float(row["mean_residual"]),
                    max_abs_zscore=float(row["max_abs_zscore"]),
                )
            )
        rows.sort(key=lambda r: r.date)
        return rows


__all__ = [
    "DayProfileRow",
    "ProfileComputer",
]
