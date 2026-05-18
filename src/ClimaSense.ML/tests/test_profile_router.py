"""FastAPI TestClient — `POST /api/profiles/analyze` wire contract."""

from __future__ import annotations

from datetime import date, datetime, timezone

import pandas as pd
from fastapi import FastAPI
from fastapi.testclient import TestClient

from climasense_ml.cursor import CursorSnapshot
from climasense_ml.profile_router import build_router
from climasense_ml.profile_persistence import PersistedDayProfileRow


def _make_app(
    *,
    history_loader,
    engine_sentinel=object(),
    cursor_as_of=datetime(2024, 5, 17, 12, 0, tzinfo=timezone.utc),
) -> FastAPI:
    app = FastAPI()
    snap = CursorSnapshot(as_of=cursor_as_of)
    app.include_router(
        build_router(
            get_engine=lambda: engine_sentinel,
            get_cursor=lambda: snap,
            history_loader=history_loader,
        )
    )
    return app


def _hourly_series(days: int, start: str = "2024-04-18") -> pd.DataFrame:
    import numpy as np

    idx = pd.date_range(start=start, periods=days * 24, freq="h", tz="UTC")
    hours = idx.hour.to_numpy()
    return pd.DataFrame(
        {
            "temperature": 20.0 + 5.0 * np.sin(2 * np.pi * hours / 24),
            "humidity": 50.0 + 5.0 * np.cos(2 * np.pi * hours / 24),
        },
        index=idx,
    )


def test_rejects_start_after_end() -> None:
    app = _make_app(history_loader=lambda: _hourly_series(10))
    with TestClient(app) as client:
        resp = client.post(
            "/api/profiles/analyze",
            json={"startDate": "2024-05-20", "endDate": "2024-05-19"},
        )
    assert resp.status_code == 400
    body = resp.json()
    assert body["error"] == "invalid_range"


def test_rejects_oversized_range() -> None:
    app = _make_app(history_loader=lambda: _hourly_series(10))
    with TestClient(app) as client:
        resp = client.post(
            "/api/profiles/analyze",
            json={"startDate": "2020-01-01", "endDate": "2030-01-01"},
        )
    assert resp.status_code == 400
    body = resp.json()
    assert body["error"] == "range_too_large"


def test_returns_camel_case_envelope(monkeypatch) -> None:
    """Successful path: orchestration runs end-to-end, response is
    `ProfilesAnalyzeResponse` with `rowsReplaced` + `rows`."""

    persisted = [
        PersistedDayProfileRow(
            day_profile_id=1,
            date=date(2024, 5, 17),
            day_of_week=4,
            mean_residual=0.001,
            max_abs_zscore=1.5,
            pattern="quiet",
            computed_at=datetime(2024, 5, 17, 12, 0, tzinfo=timezone.utc),
        )
    ]
    monkeypatch.setattr(
        "climasense_ml.profile_emitter.merge_day_profiles",
        lambda engine, rows: len(rows),
    )
    monkeypatch.setattr(
        "climasense_ml.profile_emitter.read_day_profiles_at_cursor",
        lambda engine, **kw: persisted,
    )

    app = _make_app(history_loader=lambda: _hourly_series(30))
    with TestClient(app) as client:
        resp = client.post(
            "/api/profiles/analyze",
            json={"startDate": "2024-05-17", "endDate": "2024-05-17"},
        )
    assert resp.status_code == 200, resp.text
    body = resp.json()
    assert set(body.keys()) == {"rowsReplaced", "rows"}
    assert isinstance(body["rowsReplaced"], int)
    assert len(body["rows"]) == 1
    row = body["rows"][0]
    assert row["pattern"] == "quiet"
    assert row["dayOfWeek"] == 4
    assert row["date"] == "2024-05-17"
    assert "meanResidual" in row
    assert "maxAbsZscore" in row


def test_empty_history_returns_200_with_zero_rows(monkeypatch) -> None:
    """Empty history is not an error — the handler returns a valid
    envelope with `rowsReplaced=0, rows=[]`.
    """
    monkeypatch.setattr(
        "climasense_ml.profile_emitter.read_day_profiles_at_cursor",
        lambda engine, **kw: [],
    )
    app = _make_app(history_loader=lambda: pd.DataFrame())
    with TestClient(app) as client:
        resp = client.post(
            "/api/profiles/analyze",
            json={"startDate": "2024-05-17", "endDate": "2024-05-17"},
        )
    assert resp.status_code == 200, resp.text
    body = resp.json()
    assert body["rowsReplaced"] == 0
    assert body["rows"] == []
