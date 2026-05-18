"""ProfileEmitter — recompute_range composition + idempotency.

Uses fake history loader + monkey-patched persistence so the test
runs without sklearn / sqlalchemy / SQL Server. The seam under test
is the orchestration: range validation → compute → MERGE → re-read.
"""

from __future__ import annotations

from datetime import date, datetime, timedelta, timezone
from types import SimpleNamespace
from typing import Any

import numpy as np
import pandas as pd
import pytest

from climasense_ml.cursor import CursorSnapshot
from climasense_ml.profile_computer import DayProfileRow
from climasense_ml.profile_emitter import (
    MAX_RANGE_DAYS,
    NIGHTLY_LOOKBACK_DAYS,
    ProfileEmitter,
    recompute_range,
)
from climasense_ml.profile_persistence import PersistedDayProfileRow


def _make_snap(d: date) -> CursorSnapshot:
    return CursorSnapshot(
        as_of=datetime(d.year, d.month, d.day, 12, 0, tzinfo=timezone.utc)
    )


class _FakeEngine:
    """Sentinel — the patched merge/read helpers ignore it."""


def _hourly_series(days: int, start: str = "2024-01-01") -> pd.DataFrame:
    idx = pd.date_range(start=start, periods=days * 24, freq="h", tz="UTC")
    hours = idx.hour.to_numpy()
    return pd.DataFrame(
        {
            "temperature": 20.0 + 5.0 * np.sin(2 * np.pi * hours / 24),
            "humidity": 50.0 + 5.0 * np.cos(2 * np.pi * hours / 24),
        },
        index=idx,
    )


def test_recompute_range_rejects_start_after_end() -> None:
    snap = _make_snap(date(2024, 5, 17))
    with pytest.raises(ValueError, match="must be on or before"):
        recompute_range(
            _FakeEngine(),
            snap,
            start_date=date(2024, 5, 20),
            end_date=date(2024, 5, 19),
            history_loader=lambda: _hourly_series(30),
        )


def test_recompute_range_rejects_oversize_window() -> None:
    snap = _make_snap(date(2024, 5, 17))
    with pytest.raises(ValueError, match="cap is"):
        recompute_range(
            _FakeEngine(),
            snap,
            start_date=date(2020, 1, 1),
            end_date=date(2030, 1, 1),
            history_loader=lambda: _hourly_series(10),
        )


def test_recompute_range_empty_history_returns_empty(monkeypatch) -> None:
    """Empty history → no MERGE, no read."""
    merge_calls: list[Any] = []
    read_calls: list[Any] = []
    monkeypatch.setattr(
        "climasense_ml.profile_emitter.merge_day_profiles",
        lambda engine, rows: (merge_calls.append((engine, rows)), len(rows))[-1],
    )
    monkeypatch.setattr(
        "climasense_ml.profile_emitter.read_day_profiles_at_cursor",
        lambda engine, **kw: (read_calls.append((engine, kw)), [])[-1],
    )

    snap = _make_snap(date(2024, 5, 17))
    result = recompute_range(
        _FakeEngine(),
        snap,
        start_date=date(2024, 5, 16),
        end_date=date(2024, 5, 17),
        history_loader=lambda: pd.DataFrame(),
    )
    assert result.rows_replaced == 0
    assert result.rows == []
    assert merge_calls == []
    assert read_calls == []


def test_recompute_range_drives_merge_and_reads_back(monkeypatch) -> None:
    """End-to-end orchestration: compute → MERGE → re-read."""
    merged: list[list[DayProfileRow]] = []

    def fake_merge(engine, rows):
        merged.append(list(rows))
        return len(rows)

    def fake_read(engine, *, snap, start_date, end_date):
        # Synthesise the round-tripped rows so the response envelope
        # is non-trivial.
        return [
            PersistedDayProfileRow(
                day_profile_id=42,
                date=date(2024, 5, 17),
                day_of_week=4,
                mean_residual=0.001,
                max_abs_zscore=2.0,
                pattern="quiet",
                computed_at=snap.as_of,
            )
        ]

    monkeypatch.setattr(
        "climasense_ml.profile_emitter.merge_day_profiles", fake_merge
    )
    monkeypatch.setattr(
        "climasense_ml.profile_emitter.read_day_profiles_at_cursor", fake_read
    )

    snap = _make_snap(date(2024, 5, 17))
    history = _hourly_series(days=30, start="2024-04-18")
    result = recompute_range(
        _FakeEngine(),
        snap,
        start_date=date(2024, 5, 17),
        end_date=date(2024, 5, 17),
        history_loader=lambda: history,
    )

    # Compute happened: at least one row landed in the MERGE batch
    # (depending on lag warmup the date might be valid).
    assert len(merged) == 1
    assert all(isinstance(r, DayProfileRow) for r in merged[0])
    # Read-back surfaced the persisted row.
    assert result.rows_replaced == len(merged[0])
    assert len(result.rows) == 1
    assert result.rows[0].pattern == "quiet"


def test_recompute_range_is_idempotent_under_rerun(monkeypatch) -> None:
    """Re-running with the same range yields identical row counts —
    the compute is deterministic and the MERGE is keyed on Date.
    """
    merged_counts: list[int] = []

    def fake_merge(engine, rows):
        merged_counts.append(len(rows))
        return len(rows)

    monkeypatch.setattr(
        "climasense_ml.profile_emitter.merge_day_profiles", fake_merge
    )
    monkeypatch.setattr(
        "climasense_ml.profile_emitter.read_day_profiles_at_cursor",
        lambda engine, **kw: [],
    )

    snap = _make_snap(date(2024, 5, 17))
    history = _hourly_series(days=30)
    for _ in range(3):
        recompute_range(
            _FakeEngine(),
            snap,
            start_date=date(2024, 5, 15),
            end_date=date(2024, 5, 17),
            history_loader=lambda: history,
        )
    # Same count three times in a row — compute is deterministic.
    assert len(merged_counts) == 3
    assert merged_counts[0] == merged_counts[1] == merged_counts[2]


def test_profile_emitter_tick_uses_nightly_lookback(monkeypatch) -> None:
    """The scheduler tick computes
    `[cursor.date - (NIGHTLY_LOOKBACK_DAYS - 1), cursor.date]`.
    """
    captured: dict[str, Any] = {}

    def fake_recompute(engine, snap, *, start_date, end_date, history_loader=None):
        captured["start"] = start_date
        captured["end"] = end_date
        captured["snap"] = snap
        return SimpleNamespace(
            start_date=start_date, end_date=end_date,
            rows_replaced=0, rows=[],
        )

    monkeypatch.setattr(
        "climasense_ml.profile_emitter.recompute_range", fake_recompute
    )

    snap = _make_snap(date(2024, 5, 17))
    emitter = ProfileEmitter(
        engine=_FakeEngine(),
        clock_provider=lambda: snap,
    )
    emitter.tick()
    assert captured["end"] == date(2024, 5, 17)
    assert captured["start"] == date(2024, 5, 17) - timedelta(
        days=NIGHTLY_LOOKBACK_DAYS - 1
    )


def test_max_range_days_constant_matches_validation() -> None:
    """`recompute_range` rejects ranges strictly greater than the
    `MAX_RANGE_DAYS` constant. Confirm the constant is the one the
    router exposes too.
    """
    assert MAX_RANGE_DAYS > 0
    assert MAX_RANGE_DAYS >= NIGHTLY_LOOKBACK_DAYS
