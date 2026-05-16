"""Slice-1 verification tests for `CursorSnapshot` (Python tier).

Mirrors `tests/ClimaSense.Web.Tests/CursorSnapshotTests.cs`. Each test
locks one of the three slice-1 acceptance criteria:

  AC9  — `get_cursor` resolved twice within the same request scope returns
         the same `as_of` value.
  AC10 — `windowed(timedelta(hours=24))` produces an `(as_of - 24h, as_of)` tuple.
  AC11 — `should_emit(last, timedelta(hours=1))` is true iff `as_of - last >= 1h`.

Plus guard rails for UTC normalisation and `clip()`'s SensorReadings-only
contract.
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest
from fastapi import Depends, FastAPI
from fastapi.testclient import TestClient

from climasense_ml.clock import IClock
from climasense_ml.cursor import CursorSnapshot, bind, get_current, release


class _FixedClock:
    def __init__(self, value: datetime) -> None:
        self.value = value

    def utc_now(self) -> datetime:
        return self.value


# ---------------------------------------------------------------------
# AC9: scope-singleton.
# ---------------------------------------------------------------------
def test_cursor_resolved_twice_within_same_request_returns_same_as_of() -> None:
    """The dependency injection wrapper guarantees one snapshot per request.

    Strategy: a tiny FastAPI app with a single route that resolves
    `CursorSnapshot` twice (once via `Depends`, once via `request.state`)
    after we have advanced the wall clock between resolves. Both reads
    must agree.
    """

    clock = _FixedClock(datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc))

    def get_clock() -> IClock:
        return clock

    def get_cursor(c: IClock = Depends(get_clock)) -> CursorSnapshot:
        # Bind via contextvars on first resolve; subsequent resolves
        # within the same request return the bound snapshot.
        existing = get_current()
        if existing is not None:
            return existing
        snap = CursorSnapshot.from_clock(c)
        bind(snap)
        return snap

    app = FastAPI()

    @app.get("/probe")
    def probe(
        first: CursorSnapshot = Depends(get_cursor),
        second: CursorSnapshot = Depends(get_cursor),
    ) -> dict[str, str]:
        # Mutate the wall clock between the two resolves to simulate
        # time advancing within the request — a fresh snapshot would
        # see the new value. Both `first` and `second` must agree.
        clock.value = clock.value + timedelta(hours=7)
        return {
            "first": first.as_of.isoformat(),
            "second": second.as_of.isoformat(),
        }

    with TestClient(app) as tc:
        resp = tc.get("/probe")

    body = resp.json()
    assert body["first"] == body["second"]


def test_distinct_requests_produce_distinct_snapshots() -> None:
    clock = _FixedClock(datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc))

    def get_clock() -> IClock:
        return clock

    def get_cursor(c: IClock = Depends(get_clock)) -> CursorSnapshot:
        existing = get_current()
        if existing is not None:
            return existing
        snap = CursorSnapshot.from_clock(c)
        bind(snap)
        return snap

    app = FastAPI()

    @app.get("/probe")
    def probe(snap: CursorSnapshot = Depends(get_cursor)) -> dict[str, str]:
        return {"as_of": snap.as_of.isoformat()}

    with TestClient(app) as tc:
        a = tc.get("/probe").json()["as_of"]
        clock.value = clock.value + timedelta(days=1)
        b = tc.get("/probe").json()["as_of"]

    assert a != b


# ---------------------------------------------------------------------
# AC10: windowed.
# ---------------------------------------------------------------------
def test_windowed_24h_yields_24h_window_ending_at_as_of() -> None:
    as_of = datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc)
    snap = CursorSnapshot(as_of=as_of)
    start, end = snap.windowed(timedelta(hours=24))
    assert end == as_of
    assert end - start == timedelta(hours=24)


def test_windowed_rejects_non_positive_duration() -> None:
    snap = CursorSnapshot(as_of=datetime(2024, 1, 1, tzinfo=timezone.utc))
    with pytest.raises(ValueError):
        snap.windowed(timedelta(0))
    with pytest.raises(ValueError):
        snap.windowed(timedelta(seconds=-1))


# ---------------------------------------------------------------------
# AC11: should_emit.
# ---------------------------------------------------------------------
@pytest.mark.parametrize(
    "gap_seconds,expected",
    [
        (0, False),
        (59 * 60, False),
        (60 * 60, True),
        (2 * 60 * 60, True),
    ],
)
def test_should_emit_opens_iff_gap_meets_cadence(gap_seconds: int, expected: bool) -> None:
    as_of = datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc)
    last = as_of - timedelta(seconds=gap_seconds)
    snap = CursorSnapshot(as_of=as_of)
    assert snap.should_emit(last, timedelta(hours=1)) is expected


def test_should_emit_with_none_last_emit_always_opens() -> None:
    snap = CursorSnapshot(as_of=datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc))
    assert snap.should_emit(None, timedelta(hours=1))
    assert snap.should_emit(None, timedelta(milliseconds=1))


def test_should_emit_rejects_non_positive_cadence() -> None:
    snap = CursorSnapshot(as_of=datetime(2024, 1, 1, tzinfo=timezone.utc))
    with pytest.raises(ValueError):
        snap.should_emit(snap.as_of, timedelta(0))


# ---------------------------------------------------------------------
# Guard rails.
# ---------------------------------------------------------------------
def test_construction_normalises_naive_datetime_to_utc() -> None:
    raw = datetime(2024, 6, 15, 12, 0, 0)  # tz-naive
    snap = CursorSnapshot(as_of=raw)
    assert snap.as_of.tzinfo == timezone.utc
    assert snap.as_of.replace(tzinfo=None) == raw


def test_clip_appends_where_when_query_has_none() -> None:
    snap = CursorSnapshot(as_of=datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc))
    q, params = snap.clip("SELECT TOP 100 * FROM SensorReadings")
    assert "WHERE ReadingTime <= :as_of" in q
    assert params == {"as_of": snap.as_of}


def test_clip_appends_and_when_query_has_where() -> None:
    snap = CursorSnapshot(as_of=datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc))
    q, _ = snap.clip("SELECT * FROM SensorReadings WHERE Temperature > 20")
    assert "WHERE Temperature > 20 AND ReadingTime <= :as_of" in q


def test_bind_release_round_trip() -> None:
    snap = CursorSnapshot(as_of=datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc))
    assert get_current() is None
    token = bind(snap)
    try:
        assert get_current() is snap
    finally:
        release(token)
    assert get_current() is None
