#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
#
# derive_pattern_thresholds.py — slice-9 reproducible threshold derivation.
#
# WHAT
#   Computes the three empirical percentiles baked into
#   `dbo.fn_classify_pattern` in `scripts/init-db.sql`:
#       * p90 of MaxAbsZscore   → `volatile` cut-off
#       * p25 of MeanResidual   → `cool`     cut-off (lower tail)
#       * p75 of MeanResidual   → `warm`     cut-off (upper tail)
#
#   The derivation walks the exact pipeline the production ml tier uses
#   so the numbers reproduce 1:1 between this script and a live
#   `DayProfiles` recompute over the full history:
#
#     1. Load `sensor_data.csv` via the SAME hourly-resample +
#        linear-interpolation pipeline as
#        `climasense_ml.forecaster.load_hourly_from_csv`.
#     2. Fit the lag-LR forecaster on the FULL series (no held-out
#        split — this is the production-fit code path).
#     3. Compute the residual `e_t = y_t - y_hat_t` from the in-sample
#        prediction (vectorised; no recursive multi-step here — the
#        residual that matters for "this day's deviation from its
#        own calendar cohort" is the one-step in-sample residual).
#     4. Group residuals by `(day_of_week, hour_of_day)` cohort and
#        compute the cohort mean μ_{d,h} and std σ_{d,h}. Standardise
#        each residual: z_t = (e_t - μ_{d,h}) / σ_{d,h}.
#     5. Aggregate to per-day rows: `MeanResidual = mean(e_t over day)`,
#        `MaxAbsZscore = max(|z_t| over day)`.
#     6. Compute the three percentiles across the population of
#        per-day rows.
#
#   Output: a JSON document (and a SQL snippet) the maintainer pastes
#   into `init-db.sql §4` to replace the standard-normal placeholders.
#
# RERUN INVARIANCE
#   Given a fixed `sensor_data.csv` the script produces identical
#   numbers across runs. The pipeline has no randomness — sklearn's
#   `LinearRegression` is OLS (closed form); the resampling +
#   interpolation are deterministic; percentile computation is
#   numpy-backed.
#
# USAGE
#   python3 scripts/derive_pattern_thresholds.py
#   python3 scripts/derive_pattern_thresholds.py --csv path/to/sensor_data.csv
#   python3 scripts/derive_pattern_thresholds.py --json --no-sql

from __future__ import annotations

import argparse
import json
import pathlib
import sys

import numpy as np
import pandas as pd
from sklearn.linear_model import LinearRegression


# ---------------------------------------------------------------------
# Replicate `climasense_ml.forecaster` constants + pipeline so this
# script doesn't import from the ml package (kept self-contained for
# easy CI invocation outside the uv env).
# ---------------------------------------------------------------------
LAGS: tuple[int, ...] = (1, 2, 3, 6, 12, 24, 48, 168)


def load_hourly_from_csv(csv_path: pathlib.Path) -> pd.DataFrame:
    """Replicate `forecaster.load_hourly_from_csv` exactly so the
    derivation runs against the same hourly grid the forecaster sees.
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
    try:
        df_h = df.resample("h").mean().interpolate(method="time", limit_direction="both")
    except ValueError:
        df_h = df.resample("H").mean().interpolate(method="time", limit_direction="both")
    if df_h.index.tz is None:
        df_h.index = df_h.index.tz_localize("UTC")
    return df_h


def build_features(temperature: pd.Series) -> tuple[pd.DataFrame, pd.Series]:
    """Match `forecaster.build_features` exactly."""
    frame = pd.DataFrame({"y": temperature.astype(float)})
    for lag in LAGS:
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


def compute_day_profiles(history: pd.DataFrame) -> pd.DataFrame:
    """Run the same compute the production `ProfileComputer` runs.

    Returns a DataFrame indexed by calendar date with columns
    `mean_residual`, `max_abs_zscore`, `day_of_week`.
    """
    temperature = history["temperature"].astype(float).dropna()

    # Stage 1 — fit lag-LR on the FULL series (production-fit; matches
    # the boot-fit's final coefficient set in `forecaster.fit_at_startup`
    # Stage 2). The residuals derived here therefore line up with what
    # the ml-tier's `ProfileComputer` will see when it loads the same
    # rows back from `dbo.SensorReadings`.
    x_all, y_all = build_features(temperature)
    model = LinearRegression()
    model.fit(x_all.to_numpy(), y_all.to_numpy())
    y_hat = model.predict(x_all.to_numpy())
    residuals = pd.Series(y_all.to_numpy() - y_hat, index=y_all.index, name="residual")

    # Stage 2 — calendar-cohort z-scores. The cohort is `(day_of_week,
    # hour_of_day)`. Cohorts with fewer than two samples have an
    # undefined σ; they collapse to z=0 (residual relative to a
    # one-sample mean is 0).
    cohort = pd.DataFrame(
        {
            "residual": residuals,
            "day_of_week": residuals.index.dayofweek,
            "hour_of_day": residuals.index.hour,
        }
    )
    grouped = cohort.groupby(["day_of_week", "hour_of_day"])["residual"]
    cohort["mu"] = grouped.transform("mean")
    cohort["sigma"] = grouped.transform(lambda s: s.std(ddof=1))
    # Guard divide-by-zero / NaN sigma for tiny cohorts.
    cohort["sigma"] = cohort["sigma"].fillna(0.0).clip(lower=1e-9)
    cohort["z"] = (cohort["residual"] - cohort["mu"]) / cohort["sigma"]

    # Stage 3 — per-day aggregates.
    cohort["date"] = cohort.index.date
    daily = cohort.groupby("date").agg(
        mean_residual=("residual", "mean"),
        max_abs_zscore=("z", lambda s: float(np.abs(s).max())),
        day_of_week=("day_of_week", "first"),
    )
    return daily


def derive_thresholds(daily: pd.DataFrame) -> dict[str, float]:
    """Compute the three percentiles."""
    p90_max_abs_z = float(np.percentile(daily["max_abs_zscore"], 90))
    p25_mean_res = float(np.percentile(daily["mean_residual"], 25))
    p75_mean_res = float(np.percentile(daily["mean_residual"], 75))
    return {
        "p90_max_abs_zscore": p90_max_abs_z,
        "p25_mean_residual": p25_mean_res,
        "p75_mean_residual": p75_mean_res,
    }


def format_sql_snippet(thresholds: dict[str, float], training_window: tuple[str, str]) -> str:
    """Emit a SQL snippet shaped exactly like the existing init-db.sql §4."""
    start_date, end_date = training_window
    return f"""\
-- Pattern thresholds derived empirically by scripts/derive_pattern_thresholds.py
-- over the training window {start_date} -> {end_date} (full sensor_data.csv
-- after hourly resample + linear interpolation). See SLICE-9-NOTES.md for the
-- methodology and reproducibility receipts:
--   p90(MaxAbsZscore) = {thresholds['p90_max_abs_zscore']:.6f}
--   p75(MeanResidual) = {thresholds['p75_mean_residual']:.6f}
--   p25(MeanResidual) = {thresholds['p25_mean_residual']:.6f}
--
-- Precedence: volatile > warm > cool > quiet.
CASE
    WHEN @maxAbsZscore > {thresholds['p90_max_abs_zscore']:.6f} THEN N'volatile'
    WHEN @meanResidual >  {thresholds['p75_mean_residual']:.6f} THEN N'warm'
    WHEN @meanResidual < {thresholds['p25_mean_residual']:.6f} THEN N'cool'
    ELSE N'quiet'
END
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--csv",
        type=pathlib.Path,
        default=None,
        help="Path to sensor_data.csv. Defaults to the repo-root copy.",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Emit only the JSON document (no SQL snippet).",
    )
    parser.add_argument(
        "--no-sql",
        action="store_true",
        help="Suppress the SQL snippet (still emits the human summary).",
    )
    args = parser.parse_args()

    csv_path = args.csv or (pathlib.Path(__file__).resolve().parent.parent / "sensor_data.csv")
    if not csv_path.is_file():
        print(f"derive_pattern_thresholds: {csv_path} not found", file=sys.stderr)
        return 1

    history = load_hourly_from_csv(csv_path)
    if history.empty:
        print("derive_pattern_thresholds: empty history; nothing to derive", file=sys.stderr)
        return 1

    daily = compute_day_profiles(history)
    thresholds = derive_thresholds(daily)

    training_window = (
        str(history.index.min().date()),
        str(history.index.max().date()),
    )

    payload = {
        "training_window_start": training_window[0],
        "training_window_end": training_window[1],
        "n_hourly_rows": int(len(history)),
        "n_daily_profiles": int(len(daily)),
        **thresholds,
    }

    if args.json:
        print(json.dumps(payload, indent=2, sort_keys=True))
        return 0

    print("== ClimaSense slice-9 pattern threshold derivation ==")
    print(f"  CSV:                 {csv_path}")
    print(f"  Hourly rows:         {payload['n_hourly_rows']:,}")
    print(f"  Daily profiles:      {payload['n_daily_profiles']:,}")
    print(f"  Training window:     {training_window[0]} -> {training_window[1]}")
    print()
    print("  Derived thresholds:")
    print(f"    p90(MaxAbsZscore)  = {thresholds['p90_max_abs_zscore']:.6f}")
    print(f"    p25(MeanResidual)  = {thresholds['p25_mean_residual']:.6f}")
    print(f"    p75(MeanResidual)  = {thresholds['p75_mean_residual']:.6f}")
    print()
    print("  JSON payload (for tests / SLICE-9-NOTES.md):")
    print("    " + json.dumps(payload, sort_keys=True))

    if not args.no_sql:
        print()
        print("  SQL snippet to paste into init-db.sql §4:")
        print("  --8<--")
        print(format_sql_snippet(thresholds, training_window))
        print("  --8<--")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
