"""LeaderboardSeeder — slice-6 startup task.

Populates `dbo.Leaderboard` with two kinds of rows:

  * **Notebook rows** (`Provenance = 'notebook'`) — parsed from
    `assets/results.json`. The notebook's leaderboard prints two
    blocks of text (`forecast_results` and `sequence_results`) into
    `stdouts`; both are produced by `print(df.to_string())` and carry
    `MAE / RMSE / MAPE / sMAPE` columns. The seeder concatenates
    rows from both blocks. Five canonical models — Naive,
    SeasonalNaive, Holt-Winters, ARIMA, SARIMA — plus the other
    rows the notebook printed (Rolling 24h mean, GBT, LSTM, Conv1D,
    GBT-recursive) all land with `Provenance = 'notebook'`.

  * **Live row** (`Provenance = 'live'`) — produced by calling
    `LagLinearForecaster.evaluate_on_holdout(history)` on the same
    14-day held-out window the notebook uses, then MERGE-ing a row
    with the forecaster's `model_version` as `ModelName`.

The seeder is **idempotent**: it MERGEs on `ModelName` (which is
the table's unique constraint per `init-db.sql §2.8`). Re-running
on a populated table produces zero net changes — same model name
with the same metrics is a no-op.

The seeder is **part of the FastAPI lifespan**, chained after
`LagLinearForecaster.fit_at_startup()` completes. The readiness
probe surfaces the seeder's state alongside the bootstrap and
forecaster trackers.

Interface emergence policy (ADR-0011): this is a concrete class. The
notebook-row loader and the live-row evaluator are methods on the same
class because the seeder owns both stages of the pipeline and the
shape of the data is shared. No `ILeaderboardSeeder` Protocol exists.
"""

from __future__ import annotations

import json
import logging
import pathlib
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Literal

from sqlalchemy import text

from .forecaster import LagLinearForecaster

log = logging.getLogger("climasense_ml.leaderboard")

Provenance = Literal["notebook", "live"]


# ---------------------------------------------------------------------
# Data shape — one row per (ModelName) per the schema's UNIQUE
# constraint. The notebook does not report MAPE / sMAPE for the
# sequence_results block; nullable columns reflect that.
# ---------------------------------------------------------------------
@dataclass(frozen=True)
class LeaderboardEntry:
    model_name: str
    mae: float
    rmse: float
    mape: float | None
    smape: float | None
    provenance: Provenance


# ---------------------------------------------------------------------
# Notebook parsing — the two `to_string()` blocks in
# `assets/results.json::stdouts`. Tolerant of whitespace shifts so the
# parser survives a notebook re-execution that nudges column widths.
# ---------------------------------------------------------------------
_FLOAT_RE = r"-?\d+\.\d+"
_TRAILING_FOUR_FLOATS = re.compile(
    rf"^(?P<name>.+?)\s+(?P<mae>{_FLOAT_RE})\s+(?P<rmse>{_FLOAT_RE})"
    rf"\s+(?P<mape>{_FLOAT_RE})\s+(?P<smape>{_FLOAT_RE})\s*$"
)
_TRAILING_TWO_FLOATS = re.compile(
    rf"^(?P<name>.+?)\s+(?P<mae>{_FLOAT_RE})\s+(?P<rmse>{_FLOAT_RE})\s*$"
)


def _parse_forecast_results_block(block: str) -> list[LeaderboardEntry]:
    """Parse the `forecast_results` block (MAE / RMSE / MAPE / sMAPE).

    Format (from `print(df.to_string())`):

        ```
                                    MAE   RMSE   MAPE  sMAPE
        model
        Rolling 24h mean          0.248  0.320  1.314  1.313
        Holt-Winters (add, m=24)  0.247  0.346  1.314  1.310
        ...
        ```

    The header line ("MAE RMSE ...") and the "model" row (the index
    name) are skipped because they don't match `_TRAILING_FOUR_FLOATS`.
    """
    rows: list[LeaderboardEntry] = []
    for raw_line in block.splitlines():
        line = raw_line.rstrip()
        if not line:
            continue
        m = _TRAILING_FOUR_FLOATS.match(line)
        if not m:
            continue
        name = m.group("name").strip()
        if not name or name.lower() == "model":
            continue
        rows.append(
            LeaderboardEntry(
                model_name=name,
                mae=float(m.group("mae")),
                rmse=float(m.group("rmse")),
                mape=float(m.group("mape")),
                smape=float(m.group("smape")),
                provenance="notebook",
            )
        )
    return rows


def _parse_sequence_results_block(block: str) -> list[LeaderboardEntry]:
    """Parse the `sequence_results` block (MAE / RMSE only).

    Format (from `print(df.to_string())` of a DataFrame with a numeric
    index):

        ```
                                   model       MAE      RMSE
        0       Linear regression (lags)  0.214410  0.293336
        1     Gradient boosting (1-step)  0.215482  0.304549
        ...
        ```

    We skip the header line and any line that begins with the column
    name "model" (the index header).
    """
    rows: list[LeaderboardEntry] = []
    for raw_line in block.splitlines():
        line = raw_line.rstrip()
        if not line:
            continue
        # Drop the leading integer index column ("0", "1", ...) — the
        # name we want is what follows it. We use a simple split: the
        # first token is the index; the remaining `.split()` tokens'
        # tail is two floats.
        tokens = line.split()
        if len(tokens) < 4:
            continue
        # Header row: "model MAE RMSE" — first token isn't a digit.
        if not tokens[0].lstrip("-").isdigit():
            continue
        # Strip the leading numeric index, then rejoin the rest for the
        # name-tail regex.
        rest = line.split(maxsplit=1)[1] if len(line.split(maxsplit=1)) > 1 else ""
        m = _TRAILING_TWO_FLOATS.match(rest.rstrip())
        if not m:
            continue
        name = m.group("name").strip()
        if not name:
            continue
        rows.append(
            LeaderboardEntry(
                model_name=name,
                mae=float(m.group("mae")),
                rmse=float(m.group("rmse")),
                mape=None,
                smape=None,
                provenance="notebook",
            )
        )
    return rows


def load_notebook_entries(results_json_path: pathlib.Path) -> list[LeaderboardEntry]:
    """Parse `assets/results.json` into a list of notebook leaderboard rows.

    The file's structure (committed at notebook execution time):

        ```
        {
          "stdouts": {
            "forecast_results": "<print(df.to_string()) text>",
            "sequence_results": "<print(df.to_string()) text>"
          }
        }
        ```

    Returns notebook rows ordered as in the file — `forecast_results`
    first, then `sequence_results`. Duplicate model names across the
    two blocks are preserved (the schema's UNIQUE constraint on
    `ModelName` will cause the later MERGE to update the earlier row
    in place; we sort the list before MERGEing so the order is stable).
    """
    if not results_json_path.is_file():
        raise FileNotFoundError(
            f"LeaderboardSeeder: results.json not found at {results_json_path}"
        )
    data = json.loads(results_json_path.read_text(encoding="utf-8"))
    stdouts = data.get("stdouts") or {}
    forecast_block = stdouts.get("forecast_results", "") or ""
    sequence_block = stdouts.get("sequence_results", "") or ""

    entries = _parse_forecast_results_block(forecast_block)
    entries += _parse_sequence_results_block(sequence_block)

    if not entries:
        raise ValueError(
            f"LeaderboardSeeder: no rows parsed from {results_json_path}. "
            "Check the results.json schema hasn't changed."
        )
    return entries


# ---------------------------------------------------------------------
# MERGE — idempotent per (ModelName). The schema's UQ_Leaderboard_Model
# constraint guarantees one row per name. We use SQL Server's MERGE so
# the operation is one round-trip per entry and the diagnostic count
# of rows-affected reflects only ACTUAL changes.
# ---------------------------------------------------------------------
_MERGE_SQL = text(
    """
    MERGE dbo.Leaderboard AS target
    USING (SELECT
                :model_name  AS ModelName,
                :mae         AS Mae,
                :rmse        AS Rmse,
                :mape        AS Mape,
                :smape       AS Smape,
                :provenance  AS Provenance,
                :evaluated_at AS EvaluatedAt
          ) AS source
    ON (target.ModelName = source.ModelName)
    WHEN MATCHED AND (
            target.Mae        <> source.Mae
         OR target.Rmse       <> source.Rmse
         OR ISNULL(target.Mape,  -1) <> ISNULL(source.Mape,  -1)
         OR ISNULL(target.Smape, -1) <> ISNULL(source.Smape, -1)
         OR target.Provenance <> source.Provenance
    ) THEN UPDATE SET
            Mae         = source.Mae,
            Rmse        = source.Rmse,
            Mape        = source.Mape,
            Smape       = source.Smape,
            Provenance  = source.Provenance,
            EvaluatedAt = source.EvaluatedAt
    WHEN NOT MATCHED THEN
        INSERT (ModelName, Mae, Rmse, Mape, Smape, Provenance, EvaluatedAt)
        VALUES (source.ModelName, source.Mae, source.Rmse, source.Mape,
                source.Smape, source.Provenance, source.EvaluatedAt);
    """
)


@dataclass(frozen=True)
class SeedResult:
    """Outcome of a `LeaderboardSeeder.run()` call.

    `notebook_count` and `live_count` are the row counts MERGED in
    each provenance. `changed_count` is the count of rows the MERGE
    actually inserted-or-updated — zero on idempotent re-runs.
    """

    notebook_count: int
    live_count: int
    changed_count: int
    live_mae: float | None
    live_rmse: float | None


class LeaderboardSeeder:
    """Concrete seeder. Reused across boot-fit completions; one
    construction per lifespan, one `run()` per lifespan.

    Parameters
    ----------
    engine
        SQLAlchemy engine for `dbo.Leaderboard` writes.
    results_json_path
        Filesystem path to `assets/results.json` (the notebook's
        leaderboard committed at execution time).
    forecaster
        The boot-fitted `LagLinearForecaster` whose held-out eval
        produces the `Provenance = 'live'` row. The forecaster MUST
        be already fitted — calling `run()` on an un-fitted instance
        raises `RuntimeError`.
    history_loader
        Callable returning the same hourly DataFrame the forecaster
        used at fit time. Used so the live MAE/RMSE are computed on
        exactly the same data as the boot-fit, i.e. effectively
        identical numbers (1e-12 reproducibility).
    """

    def __init__(
        self,
        *,
        engine,  # type: ignore[no-untyped-def]
        results_json_path: pathlib.Path,
        forecaster: LagLinearForecaster,
        history_loader,  # type: ignore[no-untyped-def]
    ) -> None:
        self._engine = engine
        self._results_json_path = results_json_path
        self._forecaster = forecaster
        self._history_loader = history_loader

    # -----------------------------------------------------------------
    def run(self) -> SeedResult:
        """Seed the leaderboard. Idempotent on re-run."""
        if not self._forecaster.fitted:
            raise RuntimeError(
                "LeaderboardSeeder.run() requires a fitted LagLinearForecaster. "
                "Call fit_at_startup() first."
            )

        notebook_entries = load_notebook_entries(self._results_json_path)

        # Live row: evaluate the boot-fitted forecaster on the same
        # held-out window the notebook uses (history[-336h:]). The
        # forecaster's own `evaluate_on_holdout` repeats the fit on the
        # training split so the MAE/RMSE are independent of the
        # production-fit-on-full-series step — this is what makes the
        # live row directly comparable to the notebook's lag-LR row
        # within 1e-12 (sklearn determinism).
        history = self._history_loader()
        if history is None or len(history) == 0:
            raise RuntimeError(
                "LeaderboardSeeder.run(): history_loader returned an empty "
                "DataFrame; cannot evaluate the live forecaster."
            )
        live_mae, live_rmse = self._forecaster.evaluate_on_holdout(history)

        live_entry = LeaderboardEntry(
            model_name=self._forecaster.model_version,
            mae=float(live_mae),
            rmse=float(live_rmse),
            mape=None,
            smape=None,
            provenance="live",
        )

        all_entries = [*notebook_entries, live_entry]
        evaluated_at = datetime.now(timezone.utc).replace(tzinfo=None)
        changed = 0
        with self._engine.begin() as conn:
            for entry in all_entries:
                params = {
                    "model_name": entry.model_name,
                    "mae": entry.mae,
                    "rmse": entry.rmse,
                    "mape": entry.mape,
                    "smape": entry.smape,
                    "provenance": entry.provenance,
                    "evaluated_at": evaluated_at,
                }
                result = conn.execute(_MERGE_SQL, params)
                # MERGE reports rowcount per affected row. We sum the
                # non-zero counts so re-runs (zero diff) report 0.
                if result.rowcount and result.rowcount > 0:
                    changed += result.rowcount

        out = SeedResult(
            notebook_count=len(notebook_entries),
            live_count=1,
            changed_count=changed,
            live_mae=float(live_mae),
            live_rmse=float(live_rmse),
        )

        log.info(
            "LeaderboardSeeder: merged %d notebook + %d live rows (%d changed) "
            "live MAE=%.6f RMSE=%.6f",
            out.notebook_count,
            out.live_count,
            out.changed_count,
            out.live_mae or 0.0,
            out.live_rmse or 0.0,
        )
        return out


__all__ = [
    "LeaderboardEntry",
    "LeaderboardSeeder",
    "SeedResult",
    "load_notebook_entries",
]
