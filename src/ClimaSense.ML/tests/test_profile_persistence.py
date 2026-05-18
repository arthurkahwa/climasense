"""ProfilePersistence — pinned-SQL shape + delegate behaviour.

The MERGE statement and the cursor-clipped read are the slice-9 wire
to SQL. Hand-running the strings against the fake engine gives us
type-safe coverage of the production SQL shape without a real DB.
"""

from __future__ import annotations

from datetime import date, datetime, timezone
from types import SimpleNamespace

import pytest

from climasense_ml.cursor import CursorSnapshot
from climasense_ml.profile_computer import DayProfileRow
from climasense_ml.profile_persistence import (
    merge_day_profiles,
    read_day_profiles_at_cursor,
    read_max_profile_date,
)


class _FakeConn:
    def __init__(self, fetch_result: list[tuple] | None = None, scalar_result=None) -> None:
        self.executed: list[tuple[str, dict]] = []
        self._fetch_result = fetch_result or []
        self._scalar_result = scalar_result

    def execute(self, stmt, params=None):  # type: ignore[no-untyped-def]
        self.executed.append((str(stmt), dict(params) if params else {}))
        return SimpleNamespace(
            fetchall=lambda: self._fetch_result,
            fetchone=lambda: (self._fetch_result[0] if self._fetch_result else None),
            scalar=lambda: self._scalar_result,
        )


class _FakeEngine:
    def __init__(self, fetch_result: list[tuple] | None = None, scalar_result=None) -> None:
        self.fetch_result = fetch_result or []
        self.scalar_result = scalar_result
        self.connect_calls = 0
        self.begin_calls = 0
        self.last_conn: _FakeConn | None = None

    def connect(self):  # type: ignore[no-untyped-def]
        self.connect_calls += 1
        conn = _FakeConn(self.fetch_result, self.scalar_result)
        self.last_conn = conn
        return _CM(conn)

    def begin(self):  # type: ignore[no-untyped-def]
        self.begin_calls += 1
        conn = _FakeConn(self.fetch_result, self.scalar_result)
        self.last_conn = conn
        return _CM(conn)


class _CM:
    def __init__(self, conn: _FakeConn) -> None:
        self._conn = conn

    def __enter__(self) -> _FakeConn:
        return self._conn

    def __exit__(self, *a) -> bool:
        return False


def test_merge_empty_rows_is_noop() -> None:
    engine = _FakeEngine()
    assert merge_day_profiles(engine, []) == 0
    assert engine.begin_calls == 0


def test_merge_visits_each_row_with_correct_params() -> None:
    engine = _FakeEngine()
    rows = [
        DayProfileRow(date(2024, 5, 1), 2, 0.123, 1.5),
        DayProfileRow(date(2024, 5, 2), 3, -0.05, 4.2),
    ]
    count = merge_day_profiles(engine, rows)
    assert count == 2
    assert engine.begin_calls == 1
    assert engine.last_conn is not None
    assert len(engine.last_conn.executed) == 2

    # MERGE statement shape — pinned so the SQL surface can't drift.
    stmt, params = engine.last_conn.executed[0]
    assert "MERGE dbo.DayProfiles" in stmt
    assert "fn_classify_pattern" in stmt
    assert "UQ_DayProfiles_Date" not in stmt  # constraint is implicit
    assert params["date_value"] == date(2024, 5, 1)
    assert params["day_of_week"] == 2
    assert pytest.approx(params["mean_residual"]) == 0.123
    assert pytest.approx(params["max_abs_zscore"]) == 1.5


def test_read_range_reads_through_cursor_tvf() -> None:
    fetched = [
        (1, date(2024, 5, 1), 2, 0.01, 1.2, "quiet",
         datetime(2024, 5, 1, 4, 0, tzinfo=timezone.utc)),
        (2, date(2024, 5, 2), 3, -0.10, 3.5, "volatile",
         datetime(2024, 5, 2, 4, 0, tzinfo=timezone.utc)),
    ]
    engine = _FakeEngine(fetch_result=fetched)
    snap = CursorSnapshot(as_of=datetime(2024, 5, 3, 0, 0, tzinfo=timezone.utc))

    rows = read_day_profiles_at_cursor(
        engine, snap=snap, start_date=date(2024, 5, 1), end_date=date(2024, 5, 2)
    )
    assert engine.connect_calls == 1
    stmt, params = engine.last_conn.executed[0]
    # Pinned SQL — cursor clip through TVF + window via [date >= start AND date <= end].
    assert "dbo.fv_dayprofiles_at_cursor(:as_of)" in stmt
    assert "[Date] >= :start_date" in stmt
    assert "[Date] <= :end_date" in stmt
    assert "ORDER BY [Date] ASC" in stmt
    assert params["start_date"] == date(2024, 5, 1)
    assert params["end_date"] == date(2024, 5, 2)

    assert len(rows) == 2
    assert rows[0].date == date(2024, 5, 1)
    assert rows[0].pattern == "quiet"
    assert rows[1].pattern == "volatile"
    assert rows[1].computed_at.tzinfo is timezone.utc


def test_read_max_profile_date_returns_none_when_empty() -> None:
    engine = _FakeEngine(scalar_result=None)
    assert read_max_profile_date(engine) is None


def test_read_max_profile_date_unwraps_date_value() -> None:
    engine = _FakeEngine(scalar_result=date(2024, 5, 17))
    assert read_max_profile_date(engine) == date(2024, 5, 17)


def test_read_max_profile_date_unwraps_datetime_value() -> None:
    # Some SQL drivers return DATE columns as datetimes.
    engine = _FakeEngine(scalar_result=datetime(2024, 5, 17, 0, 0))
    assert read_max_profile_date(engine) == date(2024, 5, 17)
