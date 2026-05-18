"""Tests for `ComfortEmitter` — APScheduler-driven β-prime gating.

Locks the AC: "Hourly comfort job emits one row per replay-hour as
the cursor advances." The test uses a fake engine and fake clock so
the gate's behaviour can be observed without touching SQL.

The persistence side-effect is replaced with an in-memory dict;
re-running at the same cursor MUST be a no-op for the row count but
update the value (idempotent on `BucketTime`).
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone
from typing import Any

from climasense_ml.comfort_emitter import (
    EMIT_CADENCE,
    ComfortEmitter,
    emit_comfort,
)
from climasense_ml.cursor import CursorSnapshot


class _FakeEngine:
    """In-memory stand-in for SQLAlchemy `Engine`.

    Patches just enough of the API so `read_max_bucket_time`,
    `_load_trailing_mean`, and `upsert_comfort_score` work without
    SQL Server.
    """

    def __init__(
        self,
        *,
        sensor_rows: list[tuple[datetime, float, float]] | None = None,
    ) -> None:
        self.comfort_rows: dict[datetime, dict[str, Any]] = {}
        self._sensor_rows = sensor_rows or []
        self.upserted: list[tuple[datetime, float, str, str]] = []

    def connect(self) -> "_FakeConnection":
        return _FakeConnection(self, transactional=False)

    def begin(self) -> "_FakeConnection":
        return _FakeConnection(self, transactional=True)


class _FakeConnection:
    def __init__(self, engine: _FakeEngine, *, transactional: bool) -> None:
        self._engine = engine
        self._transactional = transactional

    def __enter__(self) -> "_FakeConnection":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:  # noqa: ANN001
        del exc_type, exc, tb

    def execute(self, stmt, params=None):  # noqa: ANN001
        text = str(stmt).strip()
        upper = text.upper()
        if "MAX(BUCKETTIME)" in upper:
            return _FakeResult(scalar=_max(self._engine.comfort_rows))
        if "AVG(CAST(TEMPERATURE" in upper:
            assert params is not None
            return _FakeResult(
                row=_mean_window(
                    self._engine._sensor_rows,
                    start=params["start"],
                    end=params["end"],
                )
            )
        if "MERGE DBO.COMFORTSCORES" in upper:
            assert params is not None
            self._engine.comfort_rows[params["bucket_time"]] = dict(params)
            self._engine.upserted.append(
                (
                    params["bucket_time"],
                    params["score"],
                    params["rating"],
                    params["season"],
                )
            )
            return _FakeResult()
        raise NotImplementedError(f"FakeConnection unknown SQL: {text[:120]}")


class _FakeResult:
    def __init__(self, *, scalar=None, row=None) -> None:  # noqa: ANN001
        self._scalar = scalar
        self._row = row

    def scalar(self):  # noqa: ANN001
        return self._scalar

    def fetchone(self):  # noqa: ANN001
        return self._row


def _max(rows: dict[datetime, dict[str, Any]]) -> datetime | None:
    if not rows:
        return None
    return max(rows.keys())


def _strip_tz(d: datetime) -> datetime:
    if d.tzinfo is None:
        return d
    return d.astimezone(timezone.utc).replace(tzinfo=None)


def _mean_window(
    sensor_rows: list[tuple[datetime, float, float]],
    *,
    start: datetime,
    end: datetime,
) -> tuple[float, float, int]:
    """Return (mean_t, mean_rh, count) over `(start, end]`.

    Mirrors the SQL semantics in `_load_trailing_mean` (half-open).
    The real `_load_trailing_mean` strips tzinfo before sending to SQL
    Server, so the test fake normalises both sides to naive UTC.
    """
    s = _strip_tz(start)
    e = _strip_tz(end)
    in_window = [
        (t, rh) for (ts, t, rh) in sensor_rows if s < _strip_tz(ts) <= e
    ]
    if not in_window:
        return (None, None, 0)  # type: ignore[return-value]
    n = len(in_window)
    mean_t = sum(t for (t, _) in in_window) / n
    mean_rh = sum(rh for (_, rh) in in_window) / n
    return (mean_t, mean_rh, n)


def _snap(ts: datetime) -> CursorSnapshot:
    return CursorSnapshot(as_of=ts)


# ---------------------------------------------------------------------
# β-prime gating
# ---------------------------------------------------------------------
def test_first_tick_opens_gate_and_emits_row() -> None:
    # Sensor rows in the last hour to enable a non-empty trailing mean.
    end = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    sensor_rows = [
        (end - timedelta(minutes=k), 25.0, 50.0) for k in range(1, 60)
    ]
    engine = _FakeEngine(sensor_rows=sensor_rows)
    emitter = ComfortEmitter(
        engine=engine,
        clock_provider=lambda: _snap(end),
        hemisphere="N",
    )

    result = emitter.emit_if_due()

    assert result is not None
    assert result.bucket_time == end
    assert len(engine.upserted) == 1
    assert engine.upserted[0][2] in {
        "excellent",
        "acceptable",
        "marginal",
        "uncomfortable",
    }
    assert engine.upserted[0][3] in {"summer", "winter"}


def test_second_tick_within_cadence_holds_gate_closed() -> None:
    base = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    sensor_rows = [
        (base - timedelta(minutes=k), 25.0, 50.0) for k in range(1, 60)
    ]
    engine = _FakeEngine(sensor_rows=sensor_rows)

    # First tick at `base`.
    clock = {"now": base}
    emitter = ComfortEmitter(
        engine=engine,
        clock_provider=lambda: _snap(clock["now"]),
        hemisphere="N",
    )
    first = emitter.emit_if_due()
    assert first is not None
    assert len(engine.upserted) == 1

    # Second tick 30 minutes later — gate stays closed (cadence is 1h).
    clock["now"] = base + timedelta(minutes=30)
    second = emitter.emit_if_due()
    assert second is None
    assert len(engine.upserted) == 1  # No new row.


def test_third_tick_after_cadence_emits_a_second_row() -> None:
    base = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    # Provide enough sensor rows so two hourly windows are populated.
    sensor_rows = [
        (base + timedelta(minutes=k - 60), 25.0, 50.0) for k in range(1, 120)
    ]
    engine = _FakeEngine(sensor_rows=sensor_rows)

    clock = {"now": base}
    emitter = ComfortEmitter(
        engine=engine,
        clock_provider=lambda: _snap(clock["now"]),
        hemisphere="N",
    )
    emitter.emit_if_due()  # tick 1: emits.
    clock["now"] = base + timedelta(minutes=30)
    emitter.emit_if_due()  # tick 2: gate closed.
    # Move past the cadence — emits again.
    clock["now"] = base + EMIT_CADENCE + timedelta(minutes=1)
    third = emitter.emit_if_due()

    assert third is not None
    assert len(engine.upserted) == 2
    assert engine.upserted[0][0] != engine.upserted[1][0]


def test_emitter_swallows_exceptions_so_scheduler_keeps_ticking() -> None:
    base = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    sensor_rows = [
        (base - timedelta(minutes=k), 25.0, 50.0) for k in range(1, 60)
    ]
    engine = _FakeEngine(sensor_rows=sensor_rows)

    # Patch the engine to raise on the next MERGE.
    original_begin = engine.begin

    def _raising_begin() -> _FakeConnection:
        raise RuntimeError("synthetic DB outage")

    engine.begin = _raising_begin  # type: ignore[assignment]
    emitter = ComfortEmitter(
        engine=engine,
        clock_provider=lambda: _snap(base),
        hemisphere="N",
    )

    result = emitter.emit_if_due()

    assert result is None  # No row emitted; exception was swallowed.
    # Restore for cleanliness.
    engine.begin = original_begin  # type: ignore[assignment]


def test_emit_returns_none_when_trailing_window_is_empty() -> None:
    base = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    engine = _FakeEngine(sensor_rows=[])  # No readings at all.
    emitter = ComfortEmitter(
        engine=engine,
        clock_provider=lambda: _snap(base),
        hemisphere="N",
    )

    result = emitter.emit_if_due()

    assert result is None
    assert engine.upserted == []


# ---------------------------------------------------------------------
# Idempotency on `BucketTime` — re-running at the same cursor MERGE-
# updates the row in place rather than appending.
# ---------------------------------------------------------------------
def test_emit_at_same_cursor_is_idempotent_on_row_count() -> None:
    base = datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
    sensor_rows = [
        (base - timedelta(minutes=k), 25.0, 50.0) for k in range(1, 60)
    ]
    engine = _FakeEngine(sensor_rows=sensor_rows)
    snap = _snap(base)

    first = emit_comfort(engine, snap, hemisphere="N")
    second = emit_comfort(engine, snap, hemisphere="N")

    assert first is not None
    assert second is not None
    assert first.result == second.result
    # Both upserts wrote at the same bucket_time — only one row in storage.
    assert len(engine.comfort_rows) == 1


def test_hemisphere_flips_emitted_season_for_same_inputs() -> None:
    base_summer_in_north = datetime(2026, 7, 15, 12, 0, 0, tzinfo=timezone.utc)
    sensor_rows = [
        (base_summer_in_north - timedelta(minutes=k), 22.0, 50.0)
        for k in range(1, 60)
    ]
    engine_n = _FakeEngine(sensor_rows=sensor_rows)
    engine_s = _FakeEngine(sensor_rows=sensor_rows)
    snap = _snap(base_summer_in_north)

    res_n = emit_comfort(engine_n, snap, hemisphere="N")
    res_s = emit_comfort(engine_s, snap, hemisphere="S")

    assert res_n is not None
    assert res_s is not None
    assert res_n.result.season == "summer"
    assert res_s.result.season == "winter"
