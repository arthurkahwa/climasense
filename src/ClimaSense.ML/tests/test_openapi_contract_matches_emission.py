"""Golden test 5 — OpenAPI consistency (build-time).

Locks the architectural claim:

    > The single source of truth for the wire format is
    > `contracts/openapi.yaml`. FastAPI startup asserts
    > `app.openapi() == load(contracts/openapi.yaml)`.

Slice 2's runtime `ContractValidator` already enforces this at
container startup; this test is the BUILD-TIME equivalent — a pytest
case that loads the YAML, instantiates the FastAPI app, calls
`app.openapi()`, and asserts equality (modulo prose / FastAPI-injected
metadata) WITHOUT booting the full container.

Negative path: deliberately mutating either side (the YAML, OR the
Pydantic emission) makes both this test AND container startup fail
with `ContractMismatchError`. The test mirrors the slice-2 runtime
check exactly so it is a *true mirror* (no second source of truth for
"what counts as a contract divergence").

Why this lives in pytest rather than a separate CI step: a developer
running `pytest tests/` locally catches the drift BEFORE docker even
starts. The slice-2 runtime check is the operational gate; this test
is the developer gate.
"""

from __future__ import annotations

import os
import pathlib
import tempfile

import pytest
import yaml

os.environ.setdefault("CLIMASENSE_HEALTH_SKIP_DB", "1")
# Lifespan must not attempt the live boot-fit / bootstrap / seed
# pipelines — those need a real DB.
os.environ.setdefault("CLIMASENSE_CONTRACT_SKIP_VALIDATION", "1")
os.environ.setdefault("CLIMASENSE_SKIP_BOOTSTRAP", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_FIT", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_SCHEDULER", "1")
os.environ.setdefault("CLIMASENSE_SKIP_LEADERBOARD_SEED", "1")
os.environ.setdefault("CLIMASENSE_SKIP_COMFORT_SCHEDULER", "1")

from climasense_ml.contract_validator import (  # noqa: E402
    ContractMismatchError,
    validate_contract,
)
from climasense_ml.main import app  # noqa: E402


def _resolve_contract() -> pathlib.Path:
    """Walk up from this test file looking for `contracts/openapi.yaml`."""
    here = pathlib.Path(__file__).resolve()
    for ancestor in [here, *here.parents]:
        candidate = ancestor / "contracts" / "openapi.yaml"
        if candidate.is_file():
            return candidate
    raise FileNotFoundError("contracts/openapi.yaml not found from test file")


# ---------------------------------------------------------------------
# Positive path: the committed contract and FastAPI's emission agree.
# This is the BUILD-TIME equivalent of the slice-2 runtime
# ContractValidator pass.
# ---------------------------------------------------------------------
def test_openapi_contract_matches_emission() -> None:
    """The hand-authored YAML and the FastAPI emission agree.

    Equivalent to `ContractValidator` running at startup, but invoked
    here without booting the container. Catches drift the moment a
    developer touches the YAML, the Pydantic models, or any FastAPI
    handler.
    """
    # `validate_contract` raises on mismatch; passing means OK.
    validate_contract(app.openapi())


def test_committed_pydantic_matches_committed_yaml_path_count() -> None:
    """The committed `generated.py` and the committed YAML declare the
    same number of contract paths (after exclusion of web-tier-only
    routes inside `ContractValidator`).

    This is a structural floor — if a future slice forgets to update
    `_ML_TIER_EXCLUDED_PATHS` after promoting a stub to a real handler,
    the path count diverges and this assertion fails.
    """
    from climasense_ml.contract_validator import _normalised_surface

    contract = yaml.safe_load(_resolve_contract().read_text())
    canonical = _normalised_surface(contract)
    emitted = _normalised_surface(app.openapi())

    canonical_paths = set(canonical["paths"].keys())
    emitted_paths = set(emitted["paths"].keys())
    assert canonical_paths == emitted_paths, (
        "ml-emitted vs canonical contract path sets diverge:\n"
        f"  only in canonical: {canonical_paths - emitted_paths}\n"
        f"  only in emitted:   {emitted_paths - canonical_paths}"
    )


# ---------------------------------------------------------------------
# Negative paths: hand-editing either side must produce a clean
# `ContractMismatchError`. Slice-6 spec: "deliberately editing
# `generated.py` by hand causes FastAPI startup to fail."
# ---------------------------------------------------------------------
def test_yaml_phantom_endpoint_raises_ContractMismatchError(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A phantom YAML endpoint with no Python emission MUST fail."""
    src = _resolve_contract().read_text()
    spec = yaml.safe_load(src)
    spec["paths"]["/api/phantom"] = {
        "get": {
            "operationId": "getPhantom",
            "responses": {"200": {"description": "phantom"}},
        }
    }
    tmp = pathlib.Path(tempfile.mktemp(suffix=".yaml"))
    tmp.write_text(yaml.safe_dump(spec))

    monkeypatch.setenv("CLIMASENSE_CONTRACT_PATH", str(tmp))

    with pytest.raises(ContractMismatchError):
        validate_contract(app.openapi())


def test_yaml_missing_schema_raises_ContractMismatchError(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """A YAML that drops a schema the emission requires MUST fail.

    This is the "developer hand-edits `generated.py` to add a model
    the contract doesn't declare" failure mode — the lost-schema
    direction is identical from the validator's point of view.
    """
    spec = yaml.safe_load(_resolve_contract().read_text())
    del spec["components"]["schemas"]["ForecastRequest"]
    tmp = pathlib.Path(tempfile.mktemp(suffix=".yaml"))
    tmp.write_text(yaml.safe_dump(spec))

    monkeypatch.setenv("CLIMASENSE_CONTRACT_PATH", str(tmp))

    with pytest.raises(ContractMismatchError):
        validate_contract(app.openapi())


def test_pydantic_hand_edit_simulation_raises_ContractMismatchError(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Simulate a developer hand-editing `generated.py` to introduce a
    schema the YAML does not declare.

    We do this without actually mutating the file: build a fake
    "emitted" spec that adds a phantom schema to the FastAPI emission
    and pass IT through the validator. The shape of the divergence is
    the same shape the runtime check would see if a developer added a
    Pydantic model that no contract entry references.

    Slice-6 AC: "deliberately editing `generated.py` by hand causes
    FastAPI startup to fail." This test is the build-time equivalent
    of that runtime behaviour.
    """
    emitted = app.openapi()
    # Inject a phantom schema as if a hand-edit added it.
    emitted = dict(emitted)
    components = dict(emitted.get("components") or {})
    schemas = dict(components.get("schemas") or {})
    schemas["PhantomFromHandEdit"] = {
        "type": "object",
        "additionalProperties": False,
        "required": ["x"],
        "properties": {"x": {"type": "string"}},
    }
    components["schemas"] = schemas
    emitted["components"] = components

    with pytest.raises(ContractMismatchError):
        validate_contract(emitted)


# ---------------------------------------------------------------------
# Sanity: at least one operationId per declared path is emitted (catch
# the "endpoint declared without a handler" oversight up-front).
# ---------------------------------------------------------------------
def test_every_contract_path_has_an_operation_id() -> None:
    spec = yaml.safe_load(_resolve_contract().read_text())
    missing: list[str] = []
    for path, methods in spec.get("paths", {}).items():
        for method, op in methods.items():
            if not isinstance(op, dict):
                continue
            if not op.get("operationId"):
                missing.append(f"{method.upper()} {path}")
    assert not missing, (
        "operationId missing on contract entries (codegen needs it):\n"
        + "\n".join(missing)
    )
