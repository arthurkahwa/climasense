"""Tests for the slice-7 `/api/comfort/score` endpoint.

Locks the AC: "POST /api/ml/run/comfort recomputes scores at the
current cursor position; reruns are idempotent on (BucketTime)."

The router writes to `dbo.ComfortScores` on every call (idempotent
via MERGE on `BucketTime`). The .NET tier's `POST /api/ml/run/comfort`
proxies through to this endpoint, so the same SQL side-effects play
out under either entry point.

These tests use a fake engine + monkeypatched score_at_cursor so the
endpoint is exercised end-to-end without SQL.
"""

from __future__ import annotations

import os
from datetime import datetime, timezone

os.environ.setdefault("CLIMASENSE_HEALTH_SKIP_DB", "1")
os.environ.setdefault("CLIMASENSE_CONTRACT_SKIP_VALIDATION", "1")
os.environ.setdefault("CLIMASENSE_SKIP_BOOTSTRAP", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_FIT", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_SCHEDULER", "1")
os.environ.setdefault("CLIMASENSE_SKIP_COMFORT_SCHEDULER", "1")

from fastapi import FastAPI  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402

from climasense_ml.comfort import ComfortCalculator  # noqa: E402
from climasense_ml.comfort_router import build_router  # noqa: E402
from climasense_ml.cursor import CursorSnapshot  # noqa: E402


class _FakeEngine:
    def __init__(self) -> None:
        self.upserted: list[tuple[datetime, float, str, str]] = []

    def begin(self) -> "_FakeConn":
        return _FakeConn(self)


class _FakeConn:
    def __init__(self, engine: _FakeEngine) -> None:
        self._engine = engine

    def __enter__(self) -> "_FakeConn":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:  # noqa: ANN001
        del exc_type, exc, tb

    def execute(self, stmt, params=None):  # noqa: ANN001
        upper = str(stmt).strip().upper()
        if "MERGE DBO.COMFORTSCORES" in upper:
            assert params is not None
            self._engine.upserted.append(
                (
                    params["bucket_time"],
                    params["score"],
                    params["rating"],
                    params["season"],
                )
            )
            return None
        raise NotImplementedError(stmt)


def _build_app(engine: _FakeEngine, *, mean_t: float, mean_rh: float, hemisphere: str = "N"):
    """Construct a minimal FastAPI app with the comfort router and a
    monkeypatched `_load_trailing_mean` so the test doesn't hit SQL.
    """
    from climasense_ml import comfort_emitter

    # Monkeypatch the SQL window loader to return a fixed (T, RH, n).
    comfort_emitter._load_trailing_mean = lambda *_a, **_kw: (
        mean_t, mean_rh, 60
    )

    app = FastAPI()

    def _get_engine():
        return engine

    def _get_cursor():
        return CursorSnapshot(
            as_of=datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
        )

    def _get_hemisphere() -> str:
        return hemisphere

    app.include_router(
        build_router(
            get_engine=_get_engine,
            get_cursor=_get_cursor,
            get_hemisphere=_get_hemisphere,
        )
    )
    return app


def test_get_comfort_score_returns_camelcase_envelope_and_writes_row() -> None:
    engine = _FakeEngine()
    app = _build_app(engine, mean_t=25.0, mean_rh=50.0)

    with TestClient(app) as tc:
        resp = tc.get("/api/comfort/score")

    assert resp.status_code == 200
    body = resp.json()
    # camelCase wire shape per the contract.
    assert "score" in body
    assert "rating" in body
    assert "season" in body
    assert "bucketTime" in body
    assert "averageTemperature" in body
    assert "averageHumidity" in body
    # (25, 50, May 17 N) is inside the summer polygon → score 100.
    assert body["score"] == 100.0
    assert body["rating"] == "excellent"
    assert body["season"] == "summer"
    # The MERGE side-effect ran exactly once.
    assert len(engine.upserted) == 1
    assert engine.upserted[0][2] == "excellent"
    assert engine.upserted[0][3] == "summer"


def test_get_comfort_score_is_idempotent_on_rerun() -> None:
    engine = _FakeEngine()
    app = _build_app(engine, mean_t=25.0, mean_rh=50.0)

    with TestClient(app) as tc:
        first = tc.get("/api/comfort/score")
        second = tc.get("/api/comfort/score")

    assert first.status_code == 200
    assert second.status_code == 200
    # Same bucket_time, same MERGE — duplicate writes are no-ops on
    # row count when projected through the unique constraint.
    assert engine.upserted[0] == engine.upserted[1]


def test_southern_hemisphere_flips_season_for_may_input() -> None:
    # AC: COMFORT_HEMISPHERE=S flips the season label for the same
    # (month, T, RH) input. The cursor is May 17 (Northern summer);
    # in the southern hemisphere this becomes winter, and (25, 50)
    # then sits outside the winter polygon.
    engine = _FakeEngine()
    app = _build_app(engine, mean_t=25.0, mean_rh=50.0, hemisphere="S")

    with TestClient(app) as tc:
        resp = tc.get("/api/comfort/score")

    assert resp.status_code == 200
    body = resp.json()
    assert body["season"] == "winter"
    # The score should drop below 100 since (25, 50) is outside the
    # winter polygon.
    assert body["score"] < 100.0


def test_get_comfort_score_503_when_window_empty() -> None:
    from climasense_ml import comfort_emitter

    # Trailing window returns 0 samples.
    comfort_emitter._load_trailing_mean = lambda *_a, **_kw: (
        float("nan"), float("nan"), 0
    )

    engine = _FakeEngine()
    app = _build_app(engine, mean_t=25.0, mean_rh=50.0)
    # Re-patch after _build_app's own monkeypatch.
    comfort_emitter._load_trailing_mean = lambda *_a, **_kw: (
        float("nan"), float("nan"), 0
    )

    with TestClient(app) as tc:
        resp = tc.get("/api/comfort/score")

    assert resp.status_code == 503
    body = resp.json()
    assert body["error"] == "empty_window"
    assert engine.upserted == []
