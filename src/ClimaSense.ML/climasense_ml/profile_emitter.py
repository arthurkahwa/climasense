"""ProfileEmitter — orchestrate the slice-9 profile recompute pipeline.

Composes `ProfileComputer.compute` + `merge_day_profiles` for two
callers:

  * `POST /api/profiles/analyze` (real handler) — reviewer-driven on-
    demand recompute over `[startDate, endDate]`.
  * The nightly APScheduler cron at 03:00 UTC — recomputes the last 7
    cursor-days each night, idempotent on rerun.

Lifecycle (per tick / per request):

  1. Load the FULL hourly series from `dbo.SensorReadings` (the cohort
     μ/σ are most informative on the full population — they're stable
     across the relevant time scales).
  2. Run `ProfileComputer.compute` with `target_dates=…` so only the
     requested calendar days surface in the row set.
  3. MERGE the rows. Re-running on the same `[start, end]` yields
     identical rows (idempotent via `UQ_DayProfiles_Date`).

The SQL `CASE` classifier embeds the empirical p90/p25/p75 thresholds;
the Pattern label is therefore determined at SQL time, not at Python
time. The numeric inputs (MeanResidual, MaxAbsZscore) are computed
here.

Per ADR-0011: concrete class, no `IProfileEmitter` interface.
"""

from __future__ import annotations

import logging
import threading
from collections.abc import Callable
from dataclasses import dataclass
from datetime import date, datetime, timedelta

import pandas as pd

from .cursor import CursorSnapshot
from .forecaster import load_hourly_from_sql
from .profile_computer import DayProfileRow, ProfileComputer
from .profile_persistence import (
    PersistedDayProfileRow,
    merge_day_profiles,
    read_day_profiles_at_cursor,
)

log = logging.getLogger("climasense_ml.profile_emitter")


# How many cursor-days the nightly scheduler recomputes per tick.
# 7 matches the slice-9 spec: "computes the prior 7 replay-days each
# night, idempotent on Date". The on-demand router uses the explicit
# `[startDate, endDate]` from the request and ignores this constant.
NIGHTLY_LOOKBACK_DAYS: int = 7

# Soft upper bound on the on-demand range. The compute itself scales
# linearly with the underlying hourly history (the cohort fit is done
# once regardless of `target_dates`), so this is a sanity guard
# against catastrophic 10-year requests rather than a perf knob.
MAX_RANGE_DAYS: int = 366


@dataclass(frozen=True)
class ProfileEmissionResult:
    """Outcome of one `recompute_range` invocation."""

    start_date: date
    end_date: date
    rows_replaced: int
    rows: list[PersistedDayProfileRow]


def _dates_in_range(start: date, end: date) -> list[date]:
    """Inclusive list of calendar dates between `start` and `end`."""
    if start > end:
        raise ValueError(f"start ({start}) must be on or before end ({end})")
    span = (end - start).days
    if span + 1 > MAX_RANGE_DAYS:
        raise ValueError(
            f"range spans {span + 1} days; cap is {MAX_RANGE_DAYS} days"
        )
    return [start + timedelta(days=i) for i in range(span + 1)]


def recompute_range(
    engine,  # type: ignore[no-untyped-def]
    snap: CursorSnapshot,
    *,
    start_date: date,
    end_date: date,
    history_loader: Callable[[], pd.DataFrame] | None = None,
) -> ProfileEmissionResult:
    """Recompute + persist `DayProfiles` rows for `[start_date, end_date]`.

    Steps:
      1. `history_loader()` → full hourly history (defaults to a SQL
         load via `load_hourly_from_sql`).
      2. `ProfileComputer.compute(history, target_dates=…)`.
      3. `merge_day_profiles` (idempotent).
      4. Re-read the affected rows through
         `dbo.fv_dayprofiles_at_cursor` at the current cursor so the
         response carries the Pattern labels the CASE function
         produced.

    Idempotency: rerunning with the same `[start, end]` yields the
    same Pattern + numeric values (compute is deterministic; MERGE
    keyed on Date). The cursor-clipped read confirms this for the
    nightly scheduler's safety check.
    """
    targets = _dates_in_range(start_date, end_date)

    loader = history_loader or (lambda: load_hourly_from_sql(engine))
    history = loader()
    if history is None or history.empty:
        log.info("recompute_range: empty history; nothing to compute")
        return ProfileEmissionResult(
            start_date=start_date, end_date=end_date,
            rows_replaced=0, rows=[],
        )

    computed = ProfileComputer.compute(history, target_dates=targets)
    rows_replaced = merge_day_profiles(engine, computed)
    log.info(
        "recompute_range: start=%s end=%s computed=%d rows_replaced=%d",
        start_date.isoformat(),
        end_date.isoformat(),
        len(computed),
        rows_replaced,
    )
    rows = read_day_profiles_at_cursor(
        engine,
        snap=snap,
        start_date=start_date,
        end_date=end_date,
    )
    return ProfileEmissionResult(
        start_date=start_date,
        end_date=end_date,
        rows_replaced=rows_replaced,
        rows=rows,
    )


class ProfileEmitter:
    """APScheduler-driven nightly tick.

    Each tick recomputes the last `NIGHTLY_LOOKBACK_DAYS` cursor-days
    via `recompute_range`. The scheduler fires once per wall-day at
    03:00 UTC (one hour after the anomaly cron at 02:00 UTC).
    """

    def __init__(
        self,
        *,
        engine,  # type: ignore[no-untyped-def]
        clock_provider: Callable[[], CursorSnapshot],
        history_loader: Callable[[], pd.DataFrame] | None = None,
        lookback_days: int = NIGHTLY_LOOKBACK_DAYS,
    ) -> None:
        self._engine = engine
        self._clock_provider = clock_provider
        self._history_loader = history_loader
        self._lookback_days = int(lookback_days)
        self._lock = threading.Lock()

    def tick(self) -> ProfileEmissionResult | None:
        """One scheduler tick. Re-entrant-safe via the local lock."""
        with self._lock:
            snap = self._clock_provider()
            end_date = snap.as_of.date()
            start_date = end_date - timedelta(days=self._lookback_days - 1)
            try:
                result = recompute_range(
                    self._engine,
                    snap,
                    start_date=start_date,
                    end_date=end_date,
                    history_loader=self._history_loader,
                )
            except Exception:  # noqa: BLE001 — log + swallow so scheduler keeps ticking
                log.exception(
                    "ProfileEmitter: tick failed at cursor=%s",
                    snap.as_of.isoformat(),
                )
                return None
            log.info(
                "ProfileEmitter: tick complete cursor=%s window=[%s, %s] rows_replaced=%d",
                snap.as_of.isoformat(),
                start_date.isoformat(),
                end_date.isoformat(),
                result.rows_replaced,
            )
            return result


__all__ = [
    "NIGHTLY_LOOKBACK_DAYS",
    "MAX_RANGE_DAYS",
    "ProfileEmissionResult",
    "ProfileEmitter",
    "recompute_range",
]
