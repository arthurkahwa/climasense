"""ContractValidator — startup-time OpenAPI parity check (slice 2).

Asserts:
  * The committed `contracts/openapi.yaml` matches the FastAPI app's
    emitted spec (positive path).
  * A deliberately mutated YAML triggers `ContractMismatchError`
    (negative path — fail-fast guarantee).
  * The validator's logging contract (`ContractValidator: OK` /
    `ContractValidator: FAILED`) holds.

These tests run without a database — they enable
`CLIMASENSE_HEALTH_SKIP_DB` so the lifespan's readiness probe doesn't
attempt to dial SQL Server.
"""

from __future__ import annotations

import logging
import os
import pathlib
import tempfile

import pytest
import yaml

os.environ.setdefault("CLIMASENSE_HEALTH_SKIP_DB", "1")

# Skip the validator inside lifespan — we want to invoke it explicitly
# from the test bodies so we can both assert it passes and feed it a
# mutated YAML for the negative path.
os.environ.setdefault("CLIMASENSE_CONTRACT_SKIP_VALIDATION", "1")
# Slice 3/5/7/8: skip the various startup tasks so the test fixture
# doesn't try to bcp-load, fit, or schedule against a missing DB.
os.environ.setdefault("CLIMASENSE_SKIP_BOOTSTRAP", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_FIT", "1")
os.environ.setdefault("CLIMASENSE_SKIP_FORECAST_SCHEDULER", "1")
os.environ.setdefault("CLIMASENSE_SKIP_COMFORT_SCHEDULER", "1")
os.environ.setdefault("CLIMASENSE_SKIP_ANOMALY_SCHEDULER", "1")

from climasense_ml.contract_validator import (  # noqa: E402
    ContractMismatchError,
    validate_contract,
)
from climasense_ml.main import app  # noqa: E402


def test_committed_contract_matches_emitted_openapi() -> None:
    """The hand-authored YAML and the FastAPI emission agree."""
    # No exception == contract holds.
    validate_contract(app.openapi())


def test_validator_logs_ok_on_match(caplog: pytest.LogCaptureFixture) -> None:
    """The OK-path logs `ContractValidator: OK`."""
    with caplog.at_level(logging.INFO, logger="climasense_ml.contract_validator"):
        validate_contract(app.openapi())
    assert any(
        "ContractValidator: OK" in r.getMessage() for r in caplog.records
    ), "expected an INFO log line containing 'ContractValidator: OK'"


def _resolve_contract() -> pathlib.Path:
    here = pathlib.Path(__file__).resolve()
    for ancestor in [here, *here.parents]:
        candidate = ancestor / "contracts" / "openapi.yaml"
        if candidate.is_file():
            return candidate
    raise FileNotFoundError("contracts/openapi.yaml not found from test file")


def test_validator_raises_on_mutated_contract(monkeypatch: pytest.MonkeyPatch) -> None:
    """A phantom endpoint in the YAML must produce `ContractMismatchError`."""
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


def test_validator_logs_failed_on_mismatch(
    monkeypatch: pytest.MonkeyPatch, caplog: pytest.LogCaptureFixture
) -> None:
    """The FAIL-path logs `ContractValidator: FAILED` *before* raising.

    The deployment contract is "fail fast with a clear error message,
    not just log" — so we assert both the error log line AND the raised
    exception together.
    """
    spec = yaml.safe_load(_resolve_contract().read_text())
    # Drop a schema the emission requires — that's a real divergence.
    del spec["components"]["schemas"]["ForecastRequest"]
    tmp = pathlib.Path(tempfile.mktemp(suffix=".yaml"))
    tmp.write_text(yaml.safe_dump(spec))

    monkeypatch.setenv("CLIMASENSE_CONTRACT_PATH", str(tmp))

    with caplog.at_level(logging.ERROR, logger="climasense_ml.contract_validator"):
        with pytest.raises(ContractMismatchError):
            validate_contract(app.openapi())

    assert any(
        "ContractValidator: FAILED" in r.getMessage() for r in caplog.records
    ), "expected an ERROR log line containing 'ContractValidator: FAILED'"


def test_resolve_contract_path_finds_repo_root() -> None:
    """The default resolver walks up from the module file."""
    from climasense_ml.contract_validator import _resolve_contract_path

    p = _resolve_contract_path()
    assert p.is_file()
    assert p.name == "openapi.yaml"
    assert p.parent.name == "contracts"
