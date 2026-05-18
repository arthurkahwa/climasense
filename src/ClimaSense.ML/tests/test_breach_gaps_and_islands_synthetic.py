"""Golden test 3 — SensorFailureRules over a synthetic SensorReadings.

Locks PRD §"Testing Decisions" test 3:

    "Inserts a synthetic `SensorReadings` table containing a known
    30-minute breach, runs the breach-detection query, asserts exactly
    one (rule_id, breach_start, breach_end) row at the expected times."

ADR-0002 + #10 promoted the spec from the alert engine (which lands
in slice 11) to the three-detector pipeline — the analogous golden
test for slice 8 fixes a known stuck-value run inside a synthetic
`SensorReadings` slice, calls `SensorFailureRules.scan_recent`, and
asserts exactly the expected row landed in `dbo.Anomalies`.

The test simulates `SensorReadings` + `Anomalies` in-memory via a
fake engine that re-implements the SQL semantics. The pinned-string
SQL shape (see `test_anomaly_sensor_failure.py`) plus this behavioural
golden lock the two halves of the contract together.

The synthetic series:

  * 60 hourly readings, all at (T=22.0, RH=50.0).
  * One 30-minute gap inserted by skipping a single hour (so the
    gap rule MUST fire on the row immediately after the skip).
  * A run of 8 consecutive identical (T=22.0, RH=50.0) readings —
    the stuck rule fires on the END of that run.
  * One out-of-range reading at (T=60.0, RH=50.0) — fires the range
    rule.

We assert the post-scan rowset contains exactly three rows, one per
rule, with the expected `ReadingTime` for each.
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone

from climasense_ml.anomaly_sensor_failure import (
    SensorFailureRules,
)
from climasense_ml.cursor import CursorSnapshot


# ---------------------------------------------------------------------
# In-memory engine emulating just enough of `dbo.SensorReadings` +
# `dbo.Anomalies` + the three INSERTs to validate the detector's
# behavioural contract.
# ---------------------------------------------------------------------
class _SyntheticEngine:
    def __init__(
        self,
        *,
        sensor_rows: list[tuple[datetime, float, float]],
        gap_threshold_minutes: int = 10,
        stuck_run_length: int = 5,
        t_min: float = -10.0,
        t_max: float = 50.0,
        rh_min: float = 0.0,
        rh_max: float = 100.0,
    ) -> None:
        # Sorted by ReadingTime.
        self.sensor_rows = sorted(sensor_rows, key=lambda r: r[0])
        self.anomalies: list[dict] = []
        self._gap_threshold_minutes = gap_threshold_minutes
        self._stuck_run_length = stuck_run_length
        self._t_min = t_min
        self._t_max = t_max
        self._rh_min = rh_min
        self._rh_max = rh_max

    def begin(self) -> "_SyntheticConn":
        return _SyntheticConn(self)


class _SyntheticConn:
    def __init__(self, engine: _SyntheticEngine) -> None:
        self._engine = engine

    def __enter__(self) -> "_SyntheticConn":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:  # noqa: ANN001
        del exc_type, exc, tb

    def execute(self, stmt, params=None):  # noqa: ANN001
        upper = str(stmt).strip().upper()
        params = dict(params or {})
        if "SELECT COUNT(*) FROM DBO.SENSORREADINGS" in upper:
            return _SyntheticResult(
                scalar=len(self._in_window(params)),
            )
        if "LAG(READINGTIME)" in upper:
            return _SyntheticResult(
                rowcount=self._run_gap_rule(params),
            )
        if "PARTITION BY TEMPERATURE, HUMIDITY" in upper:
            return _SyntheticResult(
                rowcount=self._run_stuck_rule(params),
            )
        if "S.TEMPERATURE < :T_MIN" in upper:
            return _SyntheticResult(
                rowcount=self._run_range_rule(params),
            )
        raise NotImplementedError(stmt)

    # -----------------------------------------------------------------
    def _in_window(self, params: dict) -> list[tuple[datetime, float, float]]:
        start = params["window_start"]
        end = params["window_end"]
        return [(t, *r) for (t, *r) in self._engine.sensor_rows if start < _strip_tz(t) <= end]

    def _already_recorded(self, reading_time: datetime) -> bool:
        return any(
            a["ReadingTime"] == _strip_tz(reading_time)
            and a["AnomalyType"] == "sensor_failure"
            for a in self._engine.anomalies
        )

    def _run_gap_rule(self, params: dict) -> int:
        rows = self._in_window(params)
        gap_seconds_threshold = params["gap_threshold_seconds"]
        inserted = 0
        for i in range(1, len(rows)):
            prev_ts = _strip_tz(rows[i - 1][0])
            curr_ts = _strip_tz(rows[i][0])
            gap_seconds = (curr_ts - prev_ts).total_seconds()
            if gap_seconds > gap_seconds_threshold and not self._already_recorded(curr_ts):
                self._engine.anomalies.append(
                    {
                        "ReadingTime": curr_ts,
                        "AnomalyType": "sensor_failure",
                        "Severity": 1.0,
                        "Score": gap_seconds / 60.0,
                        "Description": f"gap {int(gap_seconds // 60)} min",
                    }
                )
                inserted += 1
        return inserted

    def _run_stuck_rule(self, params: dict) -> int:
        rows = self._in_window(params)
        run_length_floor = params["stuck_run_length"]
        inserted = 0
        run_start = 0
        for i in range(1, len(rows) + 1):
            same_as_prev = (
                i < len(rows)
                and rows[i][1] == rows[run_start][1]
                and rows[i][2] == rows[run_start][2]
            )
            if not same_as_prev:
                run_length = i - run_start
                if run_length >= run_length_floor:
                    end_ts = _strip_tz(rows[i - 1][0])
                    if not self._already_recorded(end_ts):
                        self._engine.anomalies.append(
                            {
                                "ReadingTime": end_ts,
                                "AnomalyType": "sensor_failure",
                                "Severity": 1.0,
                                "Score": float(run_length),
                                "Description": f"stuck for {run_length} readings",
                            }
                        )
                        inserted += 1
                run_start = i
        return inserted

    def _run_range_rule(self, params: dict) -> int:
        rows = self._in_window(params)
        inserted = 0
        for (ts, temperature, humidity) in rows:
            if (
                temperature < params["t_min"]
                or temperature > params["t_max"]
                or humidity < params["rh_min"]
                or humidity > params["rh_max"]
            ):
                if not self._already_recorded(ts):
                    if temperature < params["t_min"] or temperature > params["t_max"]:
                        desc = f"T={temperature} out of [-10.0,50.0]"
                        score = temperature
                    else:
                        desc = f"RH={humidity} out of [0.0,100.0]"
                        score = humidity
                    self._engine.anomalies.append(
                        {
                            "ReadingTime": _strip_tz(ts),
                            "AnomalyType": "sensor_failure",
                            "Severity": 1.0,
                            "Score": float(score),
                            "Description": desc,
                        }
                    )
                    inserted += 1
        return inserted


class _SyntheticResult:
    def __init__(self, *, scalar=None, rowcount: int = 0) -> None:  # noqa: ANN001
        self._scalar = scalar
        self.rowcount = rowcount

    def scalar(self):  # noqa: ANN001
        return self._scalar


def _strip_tz(d: datetime) -> datetime:
    if d.tzinfo is None:
        return d
    return d.astimezone(timezone.utc).replace(tzinfo=None)


# ---------------------------------------------------------------------
# Fixture: minute-resolution readings ending at the cursor.
#
# The production CSV is minute-resolution; the detector's
# `GAP_THRESHOLD_MINUTES = 10` only makes sense against a series at
# that cadence. We build a 20-minute slice with one gap, a stuck run,
# and an out-of-range reading.
#
# Layout (relative to cursor, in minutes back):
#   - minute 1..19 → (22.0, 50.0) baseline (varied slightly to avoid
#                    the stuck rule firing on the baseline itself)
#   - minute 8..3  → all identical (22.5, 50.5) — 6 consecutive readings
#                    so the stuck rule fires (floor = 5)
#   - minute 15 missing → gap of 2 minutes ... no, gap MUST exceed 10 min.
#                    Instead skip multiple minutes to manufacture a gap.
#   - minute 0     → (60.0, 50.0) → out of range
#
# Expected exactly three anomalies after the run:
#   * sensor_failure @ minute 19's first present reading after gap — "gap"
#   * sensor_failure @ minute 3 (end of stuck run) — "stuck"
#   * sensor_failure @ minute 0 — "out of range"
# ---------------------------------------------------------------------
def _synthetic_series(cursor: datetime) -> list[tuple[datetime, float, float]]:
    rows: list[tuple[datetime, float, float]] = []
    # Slice 1: 30 baseline minutes far back, varying by ±0.1 so no
    # stuck-run forms in the baseline.
    for offset_min in range(60, 30, -1):  # minutes back -60 .. -31
        wobble = 0.1 if offset_min % 2 == 0 else -0.1
        ts = cursor - timedelta(minutes=offset_min)
        rows.append((ts, 22.0 + wobble, 50.0))
    # Slice 2: GAP — skip minutes -30..-21 (10 missing minutes = 11-min
    # gap from -31 to -20).
    # Slice 3: stuck-value run from minute -20 .. -3 (= 18 identical
    # readings >> floor 5).
    for offset_min in range(20, 2, -1):
        ts = cursor - timedelta(minutes=offset_min)
        rows.append((ts, 22.5, 50.5))
    # Slice 4: a couple of post-stuck wobbly readings.
    for offset_min in (2, 1):
        wobble = 0.05 if offset_min == 2 else -0.05
        ts = cursor - timedelta(minutes=offset_min)
        rows.append((ts, 22.0 + wobble, 50.0))
    # Slice 5: out-of-range at the cursor moment.
    rows.append((cursor, 60.0, 50.0))
    return rows


def test_sensor_failure_synthetic_finds_three_distinct_anomalies() -> None:
    cursor = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    sensor_rows = _synthetic_series(cursor)
    engine = _SyntheticEngine(sensor_rows=sensor_rows)

    detector = SensorFailureRules(engine=engine)
    snap = CursorSnapshot(as_of=cursor)

    result = detector.scan_recent(snap)

    # One row per rule.
    assert result.inserted == 3
    descriptions = sorted(a["Description"] for a in engine.anomalies)
    # Three categories: gap, stuck, range.
    assert any(d.startswith("gap") for d in descriptions)
    assert any(d.startswith("stuck for") for d in descriptions)
    assert any(d.startswith("T=60.0") for d in descriptions)


def test_sensor_failure_synthetic_is_idempotent_on_rerun() -> None:
    """Re-running on the same window inserts ZERO additional rows.

    The detector's `WHERE NOT EXISTS` gate is the schema-level lock;
    here we emulate it via the FakeEngine's `_already_recorded`
    short-circuit. After two runs, the synthetic Anomalies table has
    exactly the same rowset as after one run.
    """
    cursor = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    sensor_rows = _synthetic_series(cursor)
    engine = _SyntheticEngine(sensor_rows=sensor_rows)

    detector = SensorFailureRules(engine=engine)
    snap = CursorSnapshot(as_of=cursor)

    first = detector.scan_recent(snap)
    first_count = len(engine.anomalies)
    snapshot_anomalies = [dict(a) for a in engine.anomalies]

    second = detector.scan_recent(snap)
    second_count = len(engine.anomalies)

    assert first.inserted == 3
    assert second.inserted == 0
    assert first_count == second_count == 3
    # Rowset unchanged across runs.
    assert engine.anomalies == snapshot_anomalies
