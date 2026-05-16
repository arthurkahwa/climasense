"""FastAPI entry point for the ML tier.

Slice 2 surface:
  * GET  /api/health/live         — process up, no dependency check.
  * GET  /api/health/ready        — DB connectivity probe.
  * GET  /api/health              — combined alias (never 503; deps in `checks`).
  * GET  /api/forecast            — slice-7 stub (501).
  * POST /api/forecast            — slice-7 stub (501).
  * POST /api/anomalies/detect    — slice-9 stub (501).
  * POST /api/profiles/analyze    — slice-10 stub (501).
  * GET  /api/comfort/score       — slice-8 stub (501).

Cross-cutting concerns wired here:
  * Structured JSON logs to stdout via `logging_setup.configure()`.
  * `X-Request-ID` middleware that mints / accepts the header and binds
    it via `contextvars` so every log line carries it.
  * `IClock` registered as a process singleton; per-request
    `CursorSnapshot` produced by the `get_cursor` dependency, which
    binds the snapshot via `cursor.bind()` for the duration of the
    request body.
  * `ContractValidator` runs on startup — `app.openapi()` is compared
    against `contracts/openapi.yaml` and any divergence raises
    `ContractMismatchError`, terminating the process. The single
    source of truth for the wire format is the YAML.
"""

from __future__ import annotations

import logging
import os
import secrets
import string
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Annotated

from fastapi import Depends, FastAPI, Request, status
from fastapi.responses import JSONResponse
from sqlalchemy import text
from sqlalchemy.exc import SQLAlchemyError
from starlette.middleware.base import BaseHTTPMiddleware

from .clock import IClock, WallClock
from .contract_validator import ContractMismatchError, validate_contract
from .cursor import CursorSnapshot, bind, release
from .db import get_engine
from .logging_setup import configure as configure_logging
from .logging_setup import reset_request_id, set_request_id
from .schemas import HealthStatus, HealthStatusEnum
from .stubs import router as stubs_router

REQUEST_ID_HEADER = "X-Request-ID"
_REQUEST_ID_ALPHABET = string.ascii_letters + string.digits


# ---------------------------------------------------------------------
# IClock singleton (slice 1: WallClock only).
# ---------------------------------------------------------------------
# TODO(slice-12): switch between WallClock and ReplayClock based on
# os.environ["CLIMASENSE_CLOCK_MODE"].
_clock: IClock = WallClock()


def get_clock() -> IClock:
    return _clock


# ---------------------------------------------------------------------
# CursorSnapshot dependency. Per CONTEXT.md, one snapshot per logical
# operation. FastAPI's `Depends` invokes this once per request body.
# ---------------------------------------------------------------------
def get_cursor(clock: Annotated[IClock, Depends(get_clock)]) -> CursorSnapshot:
    snap = CursorSnapshot.from_clock(clock)
    token = bind(snap)
    try:
        return snap
    finally:
        # `Depends` doesn't give us a tear-down hook here; we leave the
        # `release()` to a per-request middleware that wraps the whole
        # response cycle. The token is intentionally discarded — see
        # `RequestScopeMiddleware` below for the cursor lifecycle that
        # actually owns the bind/release pair.
        del token


# ---------------------------------------------------------------------
# Middlewares
# ---------------------------------------------------------------------
def _mint_request_id() -> str:
    return "".join(secrets.choice(_REQUEST_ID_ALPHABET) for _ in range(32))


def _safe_inbound(value: str | None) -> str | None:
    if not value:
        return None
    if len(value) > 128:
        return None
    for c in value:
        # Visible ASCII only; reject CR/LF / control chars (header injection).
        if not (0x20 <= ord(c) <= 0x7E):
            return None
    return value


class RequestIdMiddleware(BaseHTTPMiddleware):
    """Mint / accept `X-Request-ID` and bind it via contextvars so the
    JSON formatter can include it on every log line for the request."""

    async def dispatch(self, request: Request, call_next):  # noqa: ANN001
        inbound = _safe_inbound(request.headers.get(REQUEST_ID_HEADER))
        request_id = inbound or _mint_request_id()
        request.state.request_id = request_id

        token = set_request_id(request_id)
        try:
            response = await call_next(request)
        finally:
            reset_request_id(token)

        response.headers[REQUEST_ID_HEADER] = request_id
        return response


class CursorScopeMiddleware(BaseHTTPMiddleware):
    """Bind a `CursorSnapshot` for the lifetime of every request.

    `get_cursor` (the FastAPI dependency) sets the snapshot at handler
    entry; this middleware ensures we have a snapshot bound even for
    paths that don't depend on `get_cursor`, and cleans up the token at
    the end of the response cycle.
    """

    async def dispatch(self, request: Request, call_next):  # noqa: ANN001
        snap = CursorSnapshot.from_clock(_clock)
        token = bind(snap)
        request.state.cursor = snap
        try:
            return await call_next(request)
        finally:
            release(token)


# ---------------------------------------------------------------------
# Lifespan
# ---------------------------------------------------------------------
@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    configure_logging()
    log = logging.getLogger("climasense_ml.startup")
    log.info("ClimaSense.ML starting (slice 2: contract + stubs)")

    # ContractValidator — fail fast on any divergence between FastAPI's
    # emitted OpenAPI and the hand-authored `contracts/openapi.yaml`.
    # The validator logs `ContractValidator: OK` or `ContractValidator: FAILED`
    # before raising. Per issue #4 AC, raising terminates the process.
    skip_flag = os.environ.get("CLIMASENSE_CONTRACT_SKIP_VALIDATION", "").lower()
    if skip_flag in ("1", "true", "yes"):
        log.warning("ContractValidator: SKIPPED (CLIMASENSE_CONTRACT_SKIP_VALIDATION)")
    else:
        try:
            validate_contract(app.openapi())
        except ContractMismatchError:
            log.exception("ContractValidator: refusing to start with mismatched contract")
            raise
        except FileNotFoundError:
            log.exception(
                "ContractValidator: contracts/openapi.yaml not found; "
                "set CLIMASENSE_CONTRACT_PATH or run from the repo."
            )
            raise

    yield
    log.info("ClimaSense.ML stopping")


# ---------------------------------------------------------------------
# App
# ---------------------------------------------------------------------
app = FastAPI(
    title="ClimaSense.ML",
    version="0.2.0-slice-2",
    lifespan=lifespan,
)
app.add_middleware(CursorScopeMiddleware)
app.add_middleware(RequestIdMiddleware)

# Stub routes for the slice-2 contract surface (forecast / anomalies /
# profiles / comfort). Each returns 501 with a `ProblemDetails` body.
app.include_router(stubs_router)


def _build_health_body(
    clock: IClock, checks: dict[str, str], db_ok: bool
) -> dict[str, object]:
    """Compose a body shaped exactly like `HealthStatus`.

    We construct the dict (rather than a Pydantic instance) so the
    `service` and `status` fields use the camelCase wire spelling
    without any aliasing surprises — the response is hand-rolled JSON
    while Pydantic owns the *schema* in `schemas/generated.py`.
    """

    status_value: str
    if db_ok:
        status_value = "ok"
    elif any(v == "ok" for v in checks.values()):
        status_value = "degraded"
    else:
        status_value = "unavailable"

    return {
        "status": status_value,
        "service": "ml",
        "ts": clock.utc_now().isoformat(),
        "checks": checks,
    }


@app.get(
    "/api/health/live",
    operation_id="getHealthLive",
    tags=["health"],
    response_model=HealthStatus,
    response_model_by_alias=True,
)
async def health_live(
    clock: Annotated[IClock, Depends(get_clock)],
) -> HealthStatus:
    """Liveness probe — returns immediately, no dep checks."""
    return HealthStatus(
        status=HealthStatusEnum.ok,
        service="ml",
        ts=clock.utc_now(),  # type: ignore[arg-type]
        checks=None,
    )


@app.get(
    "/api/health/ready",
    operation_id="getHealthReady",
    tags=["health"],
    responses={
        200: {"model": HealthStatus},
        503: {"model": HealthStatus},
    },
)
async def health_ready(
    clock: Annotated[IClock, Depends(get_clock)],
) -> JSONResponse:
    """Readiness probe — confirms DB connectivity."""
    db_ok, checks = await _probe_db()
    body = _build_health_body(clock, checks, db_ok)
    code = status.HTTP_200_OK if db_ok else status.HTTP_503_SERVICE_UNAVAILABLE
    return JSONResponse(body, status_code=code)


@app.get(
    "/api/health",
    operation_id="getHealth",
    tags=["health"],
    responses={200: {"model": HealthStatus}},
)
async def health_combined(
    clock: Annotated[IClock, Depends(get_clock)],
) -> JSONResponse:
    """Combined health alias — never 503; degraded deps appear in `checks`."""
    db_ok, checks = await _probe_db()
    body = _build_health_body(clock, checks, db_ok)
    return JSONResponse(body, status_code=status.HTTP_200_OK)


async def _probe_db() -> tuple[bool, dict[str, str]]:
    """DB connectivity probe shared by `/ready` and the combined `/api/health`."""

    log = logging.getLogger("climasense_ml.health")
    checks: dict[str, str] = {}

    if _is_db_probe_disabled():
        checks["db"] = "skipped"
        return True, checks

    try:
        engine = get_engine()
        with engine.connect() as conn:
            value = conn.execute(text("SELECT 1")).scalar_one()
            db_ok = value == 1
            checks["db"] = "ok" if db_ok else "fail"
            return db_ok, checks
    except SQLAlchemyError as ex:
        log.warning("DB probe failed: %s", ex)
        checks["db"] = "fail"
        return False, checks
    except Exception as ex:  # pragma: no cover — defensive
        log.warning("DB probe raised %s: %s", type(ex).__name__, ex)
        checks["db"] = "fail"
        return False, checks


def _is_db_probe_disabled() -> bool:
    """Allow tests / dev runs to skip the live DB roundtrip."""
    flag = os.environ.get("CLIMASENSE_HEALTH_SKIP_DB", "").lower()
    return flag in ("1", "true", "yes")
