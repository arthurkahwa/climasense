"""Tests for the slice-8 `/api/anomalies/detect` endpoint.

Locks the AC: "POST /api/ml/run/anomalies returns the count of newly-
inserted rows by type via `AnomalyRunSummary`."

The router constructs the three detectors at request time from the
engine + forecaster, so the test injects factory callables that
return fakes. The HTTP-level shape (camelCase wire envelope, 503 on
un-fitted forecaster) is verified end-to-end without touching SQL.
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
os.environ.setdefault("CLIMASENSE_SKIP_ANOMALY_SCHEDULER", "1")

from dataclasses import dataclass  # noqa: E402

from fastapi import FastAPI  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402

from climasense_ml import anomaly_router  # noqa: E402
from climasense_ml.cursor import CursorSnapshot  # noqa: E402


@dataclass
class _FakeScanResult:
    inserted: int
    scanned: int


class _FakeSensor:
    def scan_recent(self, snap):  # noqa: ANN001
        return _FakeScanResult(inserted=2, scanned=100)


class _FakeResidual:
    def scan_recent(self, snap):  # noqa: ANN001
        return _FakeScanResult(inserted=1, scanned=50)


class _FakeChangepoint:
    def rescan_window(self, snap, days):  # noqa: ANN001
        return _FakeScanResult(inserted=3, scanned=90)


class _FakeForecaster:
    fitted = True


def _patch_read_recent_rows(monkeypatch, rows):  # noqa: ANN001
    monkeypatch.setattr(
        anomaly_router,
        "read_recent_rows",
        lambda engine, *, snap, since: rows,
    )


def _build_app(monkeypatch, *, forecaster=None):  # noqa: ANN001
    _patch_read_recent_rows(monkeypatch, [])

    app = FastAPI()

    def _get_engine():
        return object()

    def _get_cursor():
        return CursorSnapshot(
            as_of=datetime(2026, 5, 17, 12, 0, 0, tzinfo=timezone.utc)
        )

    def _get_forecaster():
        return forecaster

    app.include_router(
        anomaly_router.build_router(
            get_engine=_get_engine,
            get_cursor=_get_cursor,
            get_forecaster=_get_forecaster,
            sensor_failure_factory=lambda _e: _FakeSensor(),
            residual_outlier_factory=lambda _e, _f: _FakeResidual(),
            changepoint_factory=lambda _e: _FakeChangepoint(),
        )
    )
    return app


def test_post_anomalies_detect_returns_camelcase_envelope_with_per_type_counts(
    monkeypatch,
) -> None:  # noqa: ANN001
    app = _build_app(monkeypatch, forecaster=_FakeForecaster())

    with TestClient(app) as tc:
        resp = tc.post(
            "/api/anomalies/detect",
            json={"types": ["sensor_failure", "regime_shift", "residual_outlier"]},
        )

    assert resp.status_code == 200
    body = resp.json()
    # camelCase wire shape.
    assert body["inserted"] == 6
    assert body["totalScanned"] == 240
    assert "perType" in body
    assert body["perType"]["sensorFailure"] == 2
    assert body["perType"]["residualOutlier"] == 1
    assert body["perType"]["regimeShift"] == 3
    assert "rows" in body
    assert isinstance(body["rows"], list)


def test_post_anomalies_detect_returns_503_when_forecaster_not_fitted(
    monkeypatch,
) -> None:  # noqa: ANN001
    app = _build_app(monkeypatch, forecaster=None)

    with TestClient(app) as tc:
        resp = tc.post(
            "/api/anomalies/detect",
            json={"types": ["sensor_failure"]},
        )

    assert resp.status_code == 503
    body = resp.json()
    assert body["error"] == "forecaster_not_ready"


def test_post_anomalies_detect_surfaces_rows_from_read_helper(monkeypatch) -> None:  # noqa: ANN001
    from climasense_ml.anomaly_persistence import PersistedAnomalyRow

    rows = [
        PersistedAnomalyRow(
            anomaly_id=1,
            reading_time=datetime(2026, 5, 17, 11, 0, tzinfo=timezone.utc),
            anomaly_type="sensor_failure",
            severity=1.0,
            score=10.0,
            description="gap 12 min",
            detected_at=datetime(2026, 5, 17, 11, 5, tzinfo=timezone.utc),
        ),
    ]
    _patch_read_recent_rows(monkeypatch, rows)

    app = FastAPI()
    app.include_router(
        anomaly_router.build_router(
            get_engine=lambda: object(),
            get_cursor=lambda: CursorSnapshot(
                as_of=datetime(2026, 5, 17, 12, 0, tzinfo=timezone.utc)
            ),
            get_forecaster=lambda: _FakeForecaster(),
            sensor_failure_factory=lambda _e: _FakeSensor(),
            residual_outlier_factory=lambda _e, _f: _FakeResidual(),
            changepoint_factory=lambda _e: _FakeChangepoint(),
        )
    )

    with TestClient(app) as tc:
        resp = tc.post(
            "/api/anomalies/detect",
            json={"types": ["sensor_failure"]},
        )

    assert resp.status_code == 200
    body = resp.json()
    assert len(body["rows"]) == 1
    row = body["rows"][0]
    assert row["anomalyType"] == "sensor_failure"
    assert row["severity"] == 1.0
    assert row["description"] == "gap 12 min"
