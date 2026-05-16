"""Every endpoint declared in `contracts/openapi.yaml` has a stub
handler and that handler returns `501 Not Implemented` with a
contract-shaped `ProblemDetails` body.

This locks AC: "All endpoints declared in the contract have a stub
Python handler returning 501."

The test loads the contract document itself so adding a new path to
the YAML *will* fail this test until a stub handler lands — that's the
intended forcing function for slice-2 hygiene.
"""

from __future__ import annotations

import os

os.environ.setdefault("CLIMASENSE_HEALTH_SKIP_DB", "1")
os.environ.setdefault("CLIMASENSE_CONTRACT_SKIP_VALIDATION", "1")

import pathlib  # noqa: E402

import pytest  # noqa: E402
import yaml  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402

from climasense_ml.main import app  # noqa: E402


# Health endpoints are NOT 501 — they return real responses. Same for
# the SSE alerts stream which lives on the .NET tier.
_REAL_ENDPOINTS = {
    ("/api/health/live", "get"),
    ("/api/health/ready", "get"),
    ("/api/health", "get"),
    ("/api/alerts/stream", "get"),
}


def _resolve_contract() -> pathlib.Path:
    """Walk up from this test file looking for `contracts/openapi.yaml`.

    Mirrors `contract_validator._resolve_contract_path` so tests work
    regardless of pytest's cwd.
    """
    here = pathlib.Path(__file__).resolve()
    for ancestor in [here, *here.parents]:
        candidate = ancestor / "contracts" / "openapi.yaml"
        if candidate.is_file():
            return candidate
    raise FileNotFoundError("contracts/openapi.yaml not found from test file")


def _stub_routes() -> list[tuple[str, str]]:
    spec = yaml.safe_load(_resolve_contract().read_text())
    out: list[tuple[str, str]] = []
    for path, methods in spec["paths"].items():
        for method in methods:
            if method.lower() in ("get", "post", "put", "patch", "delete"):
                if (path, method.lower()) in _REAL_ENDPOINTS:
                    continue
                out.append((path, method.lower()))
    return out


def _request_body_for(method: str, path: str) -> dict[str, object]:
    """Return a minimal valid request body for stubs that need one."""
    if method != "post":
        return {}
    if path == "/api/forecast":
        return {"horizonHours": 72}
    if path == "/api/anomalies/detect":
        return {"types": ["sensor_failure"]}
    if path == "/api/profiles/analyze":
        return {"startDate": "2026-01-01", "endDate": "2026-01-14"}
    return {}


@pytest.mark.parametrize("path,method", _stub_routes())
def test_contract_endpoint_returns_501(path: str, method: str) -> None:
    with TestClient(app) as tc:
        body = _request_body_for(method, path)
        if method == "get":
            resp = tc.get(path)
        else:
            resp = tc.request(method.upper(), path, json=body)

    assert resp.status_code == 501, (
        f"{method.upper()} {path} returned {resp.status_code}; "
        f"slice-2 stubs must return 501"
    )

    body_json = resp.json()
    # ProblemDetails shape — camelCase on the wire.
    assert body_json["error"] == "not_implemented"
    assert "message" in body_json
    # `requestId` is camelCase per the contract; omitted when the
    # request didn't supply / mint one.


def test_at_least_five_stub_endpoints_are_present() -> None:
    """Sanity floor — guards against accidental removal of stub routes."""
    assert len(_stub_routes()) >= 5
