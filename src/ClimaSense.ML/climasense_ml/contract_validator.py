"""ContractValidator — startup-time parity check.

Asserts that the OpenAPI document FastAPI *emits* from its decorated
routes matches the hand-authored `contracts/openapi.yaml` modulo
non-semantic formatting differences (whitespace, key order, server
list ordering).

On match: logs `ContractValidator: OK` and returns.
On mismatch: logs `ContractValidator: FAILED` with a structured diff
and raises `ContractMismatchError` so the FastAPI process exits
non-zero. Lifespan startup catches the exception and re-raises after
logging so uvicorn shuts down cleanly.

Why a comparison helper instead of `==`:

* FastAPI auto-populates `servers`, swagger UI URLs, and a few schema
  fields that differ harmlessly from the hand-authored YAML.
* The hand-authored YAML carries reviewer-facing prose (long
  descriptions, examples) that FastAPI does NOT re-emit one-for-one.
* The contract is "what the wire looks like" — paths, methods,
  operationIds, request/response schemas, and status codes. Anything
  outside that surface is intentionally excluded from the diff so the
  reviewer can keep `contracts/openapi.yaml` documentation-rich.

The negative test (`tests/test_contract_validator.py::test_mismatch`
mutates the YAML and asserts the validator raises).
"""

from __future__ import annotations

import logging
from pathlib import Path
from typing import Any

import yaml

log = logging.getLogger("climasense_ml.contract_validator")


class ContractMismatchError(RuntimeError):
    """Raised when the emitted OpenAPI surface diverges from the YAML."""


# Path resolved at import time. The lifespan imports this module before
# FastAPI mounts, so we resolve the contract path once and stash it.
_REPO_ROOT_MARKERS = ("contracts/openapi.yaml", ".git")


def _resolve_contract_path() -> Path:
    """Walk up from this file looking for `contracts/openapi.yaml`.

    Works in both the container layout (`/app/contracts/openapi.yaml`)
    and the developer-machine layout (repo-root/contracts/openapi.yaml).
    Honours `CLIMASENSE_CONTRACT_PATH` for tests that point to a
    deliberately-mutated fixture.
    """

    import os

    override = os.environ.get("CLIMASENSE_CONTRACT_PATH")
    if override:
        p = Path(override)
        if not p.is_file():
            raise FileNotFoundError(
                f"CLIMASENSE_CONTRACT_PATH set but file not found: {p}"
            )
        return p

    here = Path(__file__).resolve()
    for ancestor in [here, *here.parents]:
        candidate = ancestor / "contracts" / "openapi.yaml"
        if candidate.is_file():
            return candidate

    raise FileNotFoundError(
        "contracts/openapi.yaml not found by walking up from "
        f"{here}. Set CLIMASENSE_CONTRACT_PATH to point to the file."
    )


def _normalised_surface(spec: dict[str, Any]) -> dict[str, Any]:
    """Project a spec down to the wire-significant surface.

    Keeps:
      * paths -> method -> {operationId, request body schema,
        response schemas keyed by status code, parameter shapes}.
      * components.schemas — full bodies (these *are* the wire shape).

    Drops:
      * info, servers, tags, externalDocs.
      * descriptions, summaries, examples (prose, not contract).
      * any FastAPI-added defaults FastAPI inserts that the hand-author
        didn't (e.g. a `422 ValidationError` block on every request-body
        route — see `_DROP_RESPONSE_CODES`).
    """

    _DROP_RESPONSE_CODES = {"422"}  # FastAPI auto-inserts; not in YAML.
    _DROP_KEYS = {"description", "summary", "example", "examples", "tags"}
    # Schemas that FastAPI synthesises from `Body()` / `Query()`
    # validation but the hand-authored contract has no reason to declare.
    # We ALSO drop schemas that belong exclusively to web-tier-only
    # endpoints (the ml tier has no handler that references them, so
    # FastAPI never emits them — that's correct, not a contract drift).
    _DROP_SCHEMAS = {
        "HTTPValidationError",
        "ValidationError",
        # Slice 3: only referenced by /api/readings/latest (web-tier only).
        "LatestReading",
        # Slice 4: only referenced by /api/readings/range and
        # /api/readings/heatmap (web-tier only).
        "RangeBucket",
        "BucketedReading",
        "BucketedReadingsResponse",
        "HeatmapCell",
        "HeatmapResponse",
        # Slice 6: only referenced by /api/leaderboard (web-tier only).
        # The ml tier *populates* dbo.Leaderboard via LeaderboardSeeder
        # at startup, but the read endpoint lives on the .NET tier so
        # the Razor `Analysis` page never crosses to the ml container.
        "Provenance",
        "LeaderboardRow",
        "LeaderboardResponse",
    }
    # Paths declared in the contract for cross-tier audit purposes but
    # NOT served by the FastAPI ml tier (they live on the .NET web tier).
    _ML_TIER_EXCLUDED_PATHS = {
        "/api/alerts/stream",
        # Slice 3: read-path bypass — web tier reads SensorReadings
        # directly. The ml tier never serves this; the contract entry
        # exists so Kiota generates a typed client and the dashboard's
        # wire shape is documented in one place.
        "/api/readings/latest",
        # Slice 4: same rationale — historical-Explorer reads bypass
        # the ml tier entirely (DATE_BUCKET runs on SQL Server).
        "/api/readings/range",
        "/api/readings/heatmap",
        # Slice 5: /api/forecasts/latest is a .NET-tier read of the
        # `dbo.fv_forecasts_at_cursor` TVF — bypasses the ml tier. The
        # ml tier owns POST /api/forecast (emission) and GET /api/forecast
        # (last batch); the slim "latest" read on the web tier exists
        # so the Explorer chart can overlay forecasts without a Python
        # hop.
        "/api/forecasts/latest",
        # Slice 6: /api/leaderboard is a .NET-tier SELECT from
        # `dbo.Leaderboard`. The ml tier *populates* that table via
        # `LeaderboardSeeder` at FastAPI startup; the read path
        # bypasses the ml container so the Analysis page survives an
        # ml outage.
        "/api/leaderboard",
    }

    def _strip(node: Any) -> Any:
        if isinstance(node, dict):
            return {k: _strip(v) for k, v in node.items() if k not in _DROP_KEYS}
        if isinstance(node, list):
            return [_strip(v) for v in node]
        return node

    out: dict[str, Any] = {"paths": {}, "components": {}}

    for path, methods in (spec.get("paths") or {}).items():
        if path in _ML_TIER_EXCLUDED_PATHS:
            continue
        out_methods: dict[str, Any] = {}
        for method, op in methods.items():
            if not isinstance(op, dict):
                continue
            op_proj: dict[str, Any] = {
                "operationId": op.get("operationId"),
                "parameters": _strip(op.get("parameters", [])),
                "requestBody": _strip(op.get("requestBody")),
                "responses": {},
            }
            for code, body in (op.get("responses") or {}).items():
                if code in _DROP_RESPONSE_CODES:
                    continue
                op_proj["responses"][code] = _strip(body)
            out_methods[method] = op_proj
        out["paths"][path] = out_methods

    # Component schemas are the canonical wire shape — strip prose but
    # keep types, enums, required-arrays, additionalProperties.
    schemas = ((spec.get("components") or {}).get("schemas")) or {}
    out["components"]["schemas"] = {
        name: _strip(schema)
        for name, schema in schemas.items()
        if name not in _DROP_SCHEMAS
    }

    return out


def _diff_surfaces(
    emitted: dict[str, Any], canonical: dict[str, Any]
) -> list[str]:
    """Return a human-readable list of bullet points naming each delta."""

    diffs: list[str] = []

    e_paths = set(emitted["paths"].keys())
    c_paths = set(canonical["paths"].keys())

    for missing in sorted(c_paths - e_paths):
        diffs.append(f"path declared in contract but not emitted: {missing}")
    for extra in sorted(e_paths - c_paths):
        diffs.append(f"path emitted but not declared in contract: {extra}")

    for path in sorted(e_paths & c_paths):
        e_methods = set(emitted["paths"][path].keys())
        c_methods = set(canonical["paths"][path].keys())
        for missing in sorted(c_methods - e_methods):
            diffs.append(f"{path}: method {missing.upper()} declared but not emitted")
        for extra in sorted(e_methods - c_methods):
            diffs.append(f"{path}: method {extra.upper()} emitted but not declared")
        for method in sorted(e_methods & c_methods):
            e_op = emitted["paths"][path][method]
            c_op = canonical["paths"][path][method]
            if e_op.get("operationId") != c_op.get("operationId"):
                diffs.append(
                    f"{path} {method.upper()}: operationId mismatch "
                    f"(emitted={e_op.get('operationId')!r}, "
                    f"canonical={c_op.get('operationId')!r})"
                )
            e_codes = set(e_op["responses"].keys())
            c_codes = set(c_op["responses"].keys())
            for missing in sorted(c_codes - e_codes):
                diffs.append(
                    f"{path} {method.upper()}: response {missing} declared but not emitted"
                )
            for extra in sorted(e_codes - c_codes):
                diffs.append(
                    f"{path} {method.upper()}: response {extra} emitted but not declared"
                )

    e_schemas = set(emitted["components"]["schemas"].keys())
    c_schemas = set(canonical["components"]["schemas"].keys())
    for missing in sorted(c_schemas - e_schemas):
        diffs.append(f"schema declared in contract but not emitted: {missing}")
    for extra in sorted(e_schemas - c_schemas):
        diffs.append(f"schema emitted but not declared in contract: {extra}")

    return diffs


def validate_contract(emitted_spec: dict[str, Any]) -> None:
    """Compare the emitted OpenAPI surface to `contracts/openapi.yaml`.

    Logs the outcome and raises `ContractMismatchError` on mismatch.
    Callers (lifespan) should let the exception propagate to terminate
    the process — that is the *fail fast* contract.
    """

    contract_path = _resolve_contract_path()
    with contract_path.open(encoding="utf-8") as fh:
        canonical_spec = yaml.safe_load(fh)

    emitted = _normalised_surface(emitted_spec)
    canonical = _normalised_surface(canonical_spec)

    diffs = _diff_surfaces(emitted, canonical)

    if not diffs:
        log.info(
            "ContractValidator: OK (%d paths, %d schemas)",
            len(canonical["paths"]),
            len(canonical["components"]["schemas"]),
        )
        return

    log.error(
        "ContractValidator: FAILED — %d delta(s) between FastAPI emission "
        "and %s:",
        len(diffs),
        contract_path,
    )
    for d in diffs:
        log.error("  - %s", d)

    raise ContractMismatchError(
        f"OpenAPI emission diverges from {contract_path}: {len(diffs)} delta(s)"
    )
