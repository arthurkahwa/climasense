"""Tests for `SensorFailureRules` SQL shape pinning + idempotency contract.

Three concerns:

  * The three SQL statements target `dbo.SensorReadings`, INSERT into
    `dbo.Anomalies` with `AnomalyType='sensor_failure'`, and each gates
    on `NOT EXISTS` against `UQ_Anomalies_TypeTime` so re-runs are
    idempotent.
  * The detector's `scan_recent` invokes all three INSERTs inside a
    single transaction.
  * Hyperparameters are stable across re-imports (regression guard).
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone

from climasense_ml import anomaly_sensor_failure
from climasense_ml.anomaly_sensor_failure import (
    GAP_THRESHOLD_MINUTES,
    HUMIDITY_MAX,
    HUMIDITY_MIN,
    SCAN_WINDOW,
    STUCK_RUN_LENGTH,
    SensorFailureRules,
    TEMPERATURE_MAX,
    TEMPERATURE_MIN,
)
from climasense_ml.cursor import CursorSnapshot


# ---------------------------------------------------------------------
# SQL shape pinning
# ---------------------------------------------------------------------
def test_gap_sql_targets_sensorreadings_and_inserts_sensor_failure() -> None:
    sql = str(anomaly_sensor_failure._GAP_SQL).upper()
    assert "FROM DBO.SENSORREADINGS" in sql
    assert "INSERT INTO DBO.ANOMALIES" in sql
    assert "'SENSOR_FAILURE'" in sql
    # Window function gate.
    assert "LAG(READINGTIME)" in sql
    # Idempotency gate — `AND NOT EXISTS (...)` chained onto the
    # per-rule WHERE clause.
    assert "NOT EXISTS" in sql
    assert "A.ANOMALYTYPE = 'SENSOR_FAILURE'" in sql
    assert "A.READINGTIME = G.READINGTIME" in sql


def test_stuck_sql_uses_gaps_and_islands_with_run_length_floor() -> None:
    sql = str(anomaly_sensor_failure._STUCK_SQL).upper()
    assert "FROM DBO.SENSORREADINGS" in sql
    assert "INSERT INTO DBO.ANOMALIES" in sql
    assert "'SENSOR_FAILURE'" in sql
    # Gaps-and-islands shape: ROW_NUMBER() partitioned by (T, RH).
    assert "ROW_NUMBER() OVER" in sql
    assert "PARTITION BY TEMPERATURE, HUMIDITY" in sql
    assert "HAVING COUNT(*) >= :STUCK_RUN_LENGTH" in sql
    # Idempotency gate.
    assert "NOT EXISTS" in sql


def test_range_sql_bounds_temperature_and_humidity() -> None:
    sql = str(anomaly_sensor_failure._RANGE_SQL).upper()
    assert "FROM  DBO.SENSORREADINGS" in sql
    assert "INSERT INTO DBO.ANOMALIES" in sql
    assert "'SENSOR_FAILURE'" in sql
    assert "S.TEMPERATURE < :T_MIN" in sql
    assert "S.TEMPERATURE > :T_MAX" in sql
    assert "S.HUMIDITY    < :RH_MIN" in sql
    assert "S.HUMIDITY   > :RH_MAX" in sql
    # Idempotency gate.
    assert "NOT EXISTS" in sql


# ---------------------------------------------------------------------
# Hyperparameter regression (these values inform the test fixture
# below and the dashboard's "scan window" copy in Index.cshtml).
# ---------------------------------------------------------------------
def test_module_constants_remain_pinned_to_known_values() -> None:
    assert SCAN_WINDOW == timedelta(hours=24)
    assert GAP_THRESHOLD_MINUTES == 10
    assert STUCK_RUN_LENGTH == 5
    assert TEMPERATURE_MIN == -10.0
    assert TEMPERATURE_MAX == 50.0
    assert HUMIDITY_MIN == 0.0
    assert HUMIDITY_MAX == 100.0


# ---------------------------------------------------------------------
# Behaviour: scan_recent invokes COUNT + three INSERTs in a single
# transaction. The fake engine records every (sql, params) tuple so
# the test can assert ordering without touching SQL Server.
# ---------------------------------------------------------------------
class _FakeResult:
    def __init__(self, *, scalar=None, rowcount: int = 0) -> None:  # noqa: ANN001
        self._scalar = scalar
        self.rowcount = rowcount

    def scalar(self):  # noqa: ANN001
        return self._scalar


class _FakeConn:
    def __init__(self, engine: "_FakeEngine") -> None:
        self._engine = engine

    def __enter__(self) -> "_FakeConn":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:  # noqa: ANN001
        del exc_type, exc, tb

    def execute(self, stmt, params=None):  # noqa: ANN001
        upper = str(stmt).strip().upper()
        self._engine.calls.append((upper, dict(params or {})))
        if "SELECT COUNT(*) FROM DBO.SENSORREADINGS" in upper:
            return _FakeResult(scalar=self._engine.scanned)
        if "LAG(READINGTIME)" in upper:
            return _FakeResult(rowcount=self._engine.gap_inserted)
        if "PARTITION BY TEMPERATURE, HUMIDITY" in upper:
            return _FakeResult(rowcount=self._engine.stuck_inserted)
        if "S.TEMPERATURE < :T_MIN" in upper:
            return _FakeResult(rowcount=self._engine.range_inserted)
        raise NotImplementedError(stmt)


class _FakeEngine:
    def __init__(
        self,
        *,
        scanned: int = 0,
        gap_inserted: int = 0,
        stuck_inserted: int = 0,
        range_inserted: int = 0,
    ) -> None:
        self.scanned = scanned
        self.gap_inserted = gap_inserted
        self.stuck_inserted = stuck_inserted
        self.range_inserted = range_inserted
        self.calls: list[tuple[str, dict]] = []

    def begin(self) -> _FakeConn:
        return _FakeConn(self)


def _snap(ts: datetime) -> CursorSnapshot:
    return CursorSnapshot(as_of=ts)


def test_scan_recent_runs_four_statements_in_order() -> None:
    engine = _FakeEngine(
        scanned=99,
        gap_inserted=2,
        stuck_inserted=1,
        range_inserted=3,
    )
    detector = SensorFailureRules(engine=engine)
    snap = _snap(datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc))

    result = detector.scan_recent(snap)

    # 1 SELECT COUNT + 3 INSERTs in order.
    assert len(engine.calls) == 4
    assert "SELECT COUNT" in engine.calls[0][0]
    assert "LAG(READINGTIME)" in engine.calls[1][0]
    assert "PARTITION BY TEMPERATURE, HUMIDITY" in engine.calls[2][0]
    assert "S.TEMPERATURE < :T_MIN" in engine.calls[3][0]

    # Aggregated counts.
    assert result.scanned == 99
    assert result.inserted == 2 + 1 + 3
    # Window is (cursor - 24h, cursor].
    assert result.window_end == snap.as_of
    assert result.window_start == snap.as_of - SCAN_WINDOW


def test_scan_recent_passes_hyperparameters_through_to_params() -> None:
    engine = _FakeEngine()
    detector = SensorFailureRules(
        engine=engine,
        gap_threshold_minutes=15,
        stuck_run_length=7,
        temperature_min=-5.0,
        temperature_max=45.0,
        humidity_min=10.0,
        humidity_max=95.0,
    )
    snap = _snap(datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc))

    detector.scan_recent(snap)

    gap_params = engine.calls[1][1]
    assert gap_params["gap_threshold_seconds"] == 15 * 60
    stuck_params = engine.calls[2][1]
    assert stuck_params["stuck_run_length"] == 7
    range_params = engine.calls[3][1]
    assert range_params["t_min"] == -5.0
    assert range_params["t_max"] == 45.0
    assert range_params["rh_min"] == 10.0
    assert range_params["rh_max"] == 95.0


def test_scan_recent_is_idempotent_on_rerun_at_same_cursor() -> None:
    # Both runs return identical insert counts because the SQL is
    # gated by NOT EXISTS — the FakeEngine simulates "second run finds
    # no new rows" by returning rowcount=0 the second time.
    class _IdempotentEngine(_FakeEngine):
        def __init__(self) -> None:
            super().__init__(
                scanned=10, gap_inserted=2, stuck_inserted=1, range_inserted=0
            )
            self._run = 0

        def begin(self) -> _FakeConn:
            self._run += 1
            if self._run > 1:
                # Second invocation: simulate idempotent re-run (no new rows).
                self.gap_inserted = 0
                self.stuck_inserted = 0
                self.range_inserted = 0
            return _FakeConn(self)

    engine = _IdempotentEngine()
    detector = SensorFailureRules(engine=engine)
    snap = _snap(datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc))

    first = detector.scan_recent(snap)
    second = detector.scan_recent(snap)

    assert first.inserted == 3
    assert second.inserted == 0
