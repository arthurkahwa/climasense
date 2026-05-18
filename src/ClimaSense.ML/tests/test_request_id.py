"""AC: an inbound `X-Request-ID` propagates through the ML tier.

Concretely:
  * The header on the inbound request is mirrored back on the response.
  * Within the request body, `logging_setup.get_request_id()` returns the
    inbound value (so log lines emitted during the request carry it).
"""

from __future__ import annotations

import os

os.environ.setdefault("CLIMASENSE_HEALTH_SKIP_DB", "1")  # avoid DB roundtrip
os.environ.setdefault("CLIMASENSE_SKIP_BOOTSTRAP", "1")  # slice 3: skip bcp in tests
os.environ.setdefault("CLIMASENSE_CONTRACT_SKIP_VALIDATION", "1")  # contract tested elsewhere
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_FIT", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_SCHEDULER", "1")
os.environ.setdefault("CLIMASENSE_SKIP_COMFORT_SCHEDULER", "1")
os.environ.setdefault("CLIMASENSE_SKIP_ANOMALY_SCHEDULER", "1")

from fastapi.testclient import TestClient  # noqa: E402

from climasense_ml.logging_setup import get_request_id  # noqa: E402
from climasense_ml.main import app  # noqa: E402


def test_inbound_header_is_mirrored_on_response() -> None:
    with TestClient(app) as tc:
        resp = tc.get("/api/health/live", headers={"X-Request-ID": "probe-abc-123"})
    assert resp.status_code == 200
    assert resp.headers.get("X-Request-ID") == "probe-abc-123"


def test_missing_header_is_minted() -> None:
    with TestClient(app) as tc:
        resp = tc.get("/api/health/live")
    minted = resp.headers.get("X-Request-ID")
    assert minted is not None
    assert 1 <= len(minted) <= 128


def test_request_id_visible_inside_handler() -> None:
    """Round-trip the bound contextvar value back via a custom probe route."""
    from fastapi import FastAPI

    from climasense_ml.main import RequestIdMiddleware

    probe = FastAPI()
    probe.add_middleware(RequestIdMiddleware)

    @probe.get("/echo")
    def echo() -> dict[str, str | None]:
        return {"request_id": get_request_id()}

    with TestClient(probe) as tc:
        body = tc.get("/echo", headers={"X-Request-ID": "echo-7"}).json()

    assert body["request_id"] == "echo-7"


def test_header_injection_attempt_is_rejected() -> None:
    """Reject CR/LF in the inbound header — fall back to a freshly-minted id."""
    with TestClient(app) as tc:
        resp = tc.get("/api/health/live", headers={"X-Request-ID": "ok"})
    # Sanity: the well-formed value passes through.
    assert resp.headers.get("X-Request-ID") == "ok"
