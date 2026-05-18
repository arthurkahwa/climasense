"""Slice-3 verification tests for the bootstrap-state machine and the
readiness probe's bootstrap-aware gating.

Locks:

  * `_probe_bootstrap` returns False for `pending` / `in_progress` /
    `failed`; True for `complete` / `skipped`.
  * `/api/health/ready` returns 503 while bootstrap is `in_progress`
    even when the DB roundtrip succeeds — the dashboard cannot reach
    a populated `SensorReadings` before bootstrap finishes.
  * `/api/health/ready` returns 200 once bootstrap state flips to
    `complete` (the happy first-boot path) or `skipped` (the
    idempotent re-boot path).
  * `/api/health/live` is independent of bootstrap state — process-up
    is process-up.
"""

from __future__ import annotations

import os

os.environ.setdefault("CLIMASENSE_HEALTH_SKIP_DB", "1")
os.environ.setdefault("CLIMASENSE_CONTRACT_SKIP_VALIDATION", "1")
os.environ.setdefault("CLIMASENSE_SKIP_BOOTSTRAP", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_FIT", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_SCHEDULER", "1")
os.environ.setdefault("CLIMASENSE_SKIP_COMFORT_SCHEDULER", "1")
os.environ.setdefault("CLIMASENSE_SKIP_ANOMALY_SCHEDULER", "1")

import pytest  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402

from climasense_ml.main import app, get_bootstrap_state  # noqa: E402


@pytest.fixture(autouse=True)
def _restore_bootstrap_state():
    """Snapshot+restore the singleton across tests so order is irrelevant."""
    tracker = get_bootstrap_state()
    saved_state = tracker.state
    saved_detail = tracker.detail
    saved_rows = tracker.rows_loaded
    try:
        yield
    finally:
        tracker.mark(saved_state, detail=saved_detail, rows_loaded=saved_rows)


# NB: The lifespan runs once when the TestClient context opens and
# wires the bootstrap state to "skipped" (CLIMASENSE_SKIP_BOOTSTRAP=1).
# We mutate the state *inside* the context to model what the live
# system observes mid-bootstrap.
def test_live_probe_ignores_bootstrap_state() -> None:
    tracker = get_bootstrap_state()
    with TestClient(app) as tc:
        tracker.mark("in_progress", detail="bcp running", rows_loaded=None)
        resp = tc.get("/api/health/live")
    assert resp.status_code == 200
    assert resp.json()["status"] == "ok"


def test_ready_probe_503_while_bootstrap_in_progress() -> None:
    tracker = get_bootstrap_state()
    with TestClient(app) as tc:
        tracker.mark("in_progress", detail="bcp running", rows_loaded=None)
        resp = tc.get("/api/health/ready")
    assert resp.status_code == 503
    body = resp.json()
    assert body["checks"]["bootstrap"] == "skipped"


def test_ready_probe_503_when_bootstrap_failed() -> None:
    tracker = get_bootstrap_state()
    with TestClient(app) as tc:
        tracker.mark("failed", detail="bcp not on PATH", rows_loaded=None)
        resp = tc.get("/api/health/ready")
    assert resp.status_code == 503
    body = resp.json()
    assert body["checks"]["bootstrap"] == "fail"


def test_ready_probe_200_when_bootstrap_complete() -> None:
    tracker = get_bootstrap_state()
    with TestClient(app) as tc:
        tracker.mark(
            "complete",
            detail="bcp loaded 2450000 rows",
            rows_loaded=2_450_000,
        )
        resp = tc.get("/api/health/ready")
    assert resp.status_code == 200
    body = resp.json()
    assert body["status"] == "ok"
    assert body["checks"]["bootstrap"] == "ok"


def test_ready_probe_200_when_bootstrap_skipped() -> None:
    """Idempotent re-boot path: row-count probe found data, no bcp run."""
    tracker = get_bootstrap_state()
    with TestClient(app) as tc:
        tracker.mark(
            "skipped",
            detail="SensorReadings already populated (probe returned 2450000)",
            rows_loaded=2_450_000,
        )
        resp = tc.get("/api/health/ready")
    assert resp.status_code == 200
    body = resp.json()
    assert body["status"] == "ok"
    assert body["checks"]["bootstrap"] == "ok"


def test_combined_health_never_returns_503() -> None:
    tracker = get_bootstrap_state()
    with TestClient(app) as tc:
        tracker.mark("in_progress", detail="bcp running")
        resp = tc.get("/api/health")
    # Slice-1 contract for the combined alias: never 503; status flags
    # the issue via `checks` and the coarse `status` enum.
    assert resp.status_code == 200
    body = resp.json()
    assert body["checks"]["bootstrap"] == "skipped"
    assert body["status"] in ("ok", "degraded", "unavailable")
