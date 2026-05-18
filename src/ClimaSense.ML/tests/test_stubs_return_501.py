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
# Slice 3: TestClient triggers the lifespan; skip the live bcp bootstrap.
os.environ.setdefault("CLIMASENSE_SKIP_BOOTSTRAP", "1")
# Slice 5: skip the lag-LR boot-fit (no DB available in this test).
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_FIT", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_SCHEDULER", "1")
# Slice 7: skip the comfort scheduler (no DB available in this test).
os.environ.setdefault("CLIMASENSE_SKIP_COMFORT_SCHEDULER", "1")
# Slice 8: skip the nightly anomaly scheduler.
os.environ.setdefault("CLIMASENSE_SKIP_ANOMALY_SCHEDULER", "1")

import pathlib  # noqa: E402

import pytest  # noqa: E402
import yaml  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402

from climasense_ml.main import app  # noqa: E402


# Health endpoints are NOT 501 — they return real responses. Same for
# endpoints served only by the .NET web tier (SSE stream + slice-3's
# /api/readings/latest read-path bypass).
_REAL_ENDPOINTS = {
    ("/api/health/live", "get"),
    ("/api/health/ready", "get"),
    ("/api/health", "get"),
    ("/api/alerts/stream", "get"),
    # Slice 3: declared in the contract for documentation + .NET
    # codegen, but the read path bypasses the ml tier per ADR-0010.
    ("/api/readings/latest", "get"),
    # Slice 4: range + heatmap, same web-tier-only rationale.
    ("/api/readings/range", "get"),
    ("/api/readings/heatmap", "get"),
    # Slice 5: /api/forecast GET + POST are REAL ml-tier endpoints now
    # (lag-LR boot-fit + emission). Not stubs.
    ("/api/forecast", "get"),
    ("/api/forecast", "post"),
    # Slice 5: /api/forecasts/latest is web-tier read-path bypass.
    ("/api/forecasts/latest", "get"),
    # Slice 6: /api/leaderboard is web-tier read-path bypass. The ml
    # tier populates `dbo.Leaderboard` via `LeaderboardSeeder` at
    # startup; the read SELECT is served by the .NET tier directly.
    ("/api/leaderboard", "get"),
    # Slice 7: /api/comfort/score is a REAL ml-tier endpoint (ASHRAE
    # 55 graphical zone scoring + upsert into ComfortScores). Not a
    # stub anymore.
    ("/api/comfort/score", "get"),
    # Slice 7: /api/comfort/current is a .NET web-tier read of the
    # `dbo.fv_comfortscores_at_cursor` TVF — bypasses the ml tier.
    ("/api/comfort/current", "get"),
    # Slice 8: /api/anomalies/detect is a REAL ml-tier endpoint now
    # (three-detector pipeline + cursor-clipped read of the rows that
    # landed). Not a stub anymore.
    ("/api/anomalies/detect", "post"),
    # Slice 8: /api/anomalies/latest + /api/anomalies are .NET web-tier
    # reads of the `dbo.fv_anomalies_at_cursor` TVF — both bypass the
    # ml tier.
    ("/api/anomalies/latest", "get"),
    ("/api/anomalies", "get"),
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


def test_at_least_one_stub_endpoint_is_present() -> None:
    """Sanity floor — guards against accidental removal of stub routes.

    Slice 5 promoted /api/forecast GET + POST from stubs to real
    handlers; slice 7 promoted /api/comfort/score; slice 8 promoted
    /api/anomalies/detect. Profiles remains stubbed (slice 10).
    """
    assert len(_stub_routes()) >= 1
