"""FastAPI entry point for the ML tier.

Slice 3 surface (everything from slice 2 plus the bootstrap pipeline):
  * GET  /api/health/live         — process up, no dependency check.
  * GET  /api/health/ready        — DB connectivity AND bootstrap-complete probe.
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
  * `IngestionService.bootstrap_from_csv_if_empty()` runs as a
    background task during the lifespan startup. The HTTP server
    accepts traffic immediately so `/api/health/live` returns 200,
    but `/api/health/ready` reports 503 with `bootstrap=in_progress`
    until the bcp load completes. This keeps the slice-1 healthcheck
    semantics aligned with the slice-3 first-boot timing budget
    (30–90 s for ~2.45 M rows).
"""

from __future__ import annotations

import asyncio
import logging
import os
import pathlib
import secrets
import string
import subprocess
import threading
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Annotated, Literal

from fastapi import Depends, FastAPI, Request, status
from fastapi.responses import JSONResponse
from sqlalchemy import text
from sqlalchemy.exc import SQLAlchemyError
from starlette.middleware.base import BaseHTTPMiddleware

from .clock import IClock, WallClock
from .contract_validator import ContractMismatchError, validate_contract
from .cursor import CursorSnapshot, bind, release
from .db import get_engine
from .ingestion import BcpUnavailableError, IngestionService
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
# Bootstrap state machine (slice 3).
#
# The single-process invariant is enforced by `threading.Lock` —
# uvicorn's worker is one process so the lock is sufficient. The state
# is observed by `/api/health/ready` and never mutated outside this
# module.
#
# States:
#   * "pending"     — startup hasn't finished its first probe yet.
#   * "in_progress" — `bootstrap_from_csv_if_empty()` is running.
#   * "skipped"     — the row-count probe found existing data; idempotent path.
#   * "complete"    — bcp returned 0 and the table is populated.
#   * "failed"      — any exception during bootstrap. The error message is
#                     captured for the readiness probe.
# ---------------------------------------------------------------------
BootstrapState = Literal["pending", "in_progress", "skipped", "complete", "failed"]


class _BootstrapTracker:
    """Process-singleton holder for bootstrap state. Thread-safe."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._state: BootstrapState = "pending"
        self._detail: str = "bootstrap has not been attempted yet"
        self._rows_loaded: int | None = None

    @property
    def state(self) -> BootstrapState:
        with self._lock:
            return self._state

    @property
    def detail(self) -> str:
        with self._lock:
            return self._detail

    @property
    def rows_loaded(self) -> int | None:
        with self._lock:
            return self._rows_loaded

    def mark(
        self,
        state: BootstrapState,
        *,
        detail: str,
        rows_loaded: int | None = None,
    ) -> None:
        with self._lock:
            self._state = state
            self._detail = detail
            if rows_loaded is not None:
                self._rows_loaded = rows_loaded


_bootstrap = _BootstrapTracker()


def get_bootstrap_state() -> _BootstrapTracker:
    """Public accessor for tests and the readiness probe."""
    return _bootstrap


def _build_ingestion_service() -> IngestionService:
    """Construct an `IngestionService` wired against the real DB engine
    and the container-installed `bcp` binary.

    Tests construct their own `IngestionService` with fakes — this
    factory is for the lifespan call only.
    """

    csv_path = pathlib.Path(
        os.environ.get("CLIMASENSE_BOOTSTRAP_CSV", "/data/sensor_data.csv")
    )
    seed_path = pathlib.Path(
        os.environ.get("CLIMASENSE_SEED_CSV", "/tmp/seed.csv")
    )

    bcp_settings = {
        "server": (
            f"{os.environ.get('CLIMASENSE_DB_HOST', 'db')},"
            f"{os.environ.get('CLIMASENSE_DB_PORT', '1433')}"
        ),
        "user": os.environ.get("CLIMASENSE_DB_USER", "sa"),
        "password": os.environ.get("CLIMASENSE_DB_PASSWORD", ""),
        "database": os.environ.get("CLIMASENSE_DB_NAME", "ClimaSense"),
    }

    def _count_sensor_rows() -> int:
        engine = get_engine()
        with engine.connect() as conn:
            # `SELECT TOP 1 1` short-circuits cheaper than COUNT(*) for
            # the "non-empty" check the bootstrap actually needs. We
            # still translate to a row count for log clarity.
            value = conn.execute(
                text("SELECT TOP 1 1 FROM dbo.SensorReadings")
            ).scalar()
            return 1 if value == 1 else 0

    def _run_bcp(argv: list[str], *, env: dict[str, str]) -> object:
        # subprocess.run is the canonical "shell out and wait" call;
        # the IngestionService treats the return type by ducktype so
        # the slice-3 tests can swap a `_FakeBcp` in.
        return subprocess.run(  # noqa: S603
            argv,
            env=env,
            capture_output=True,
            text=True,
            check=False,
        )

    return IngestionService(
        csv_path=csv_path,
        seed_csv_path=seed_path,
        row_counter=_count_sensor_rows,
        bcp_runner=_run_bcp,
        bcp_settings=bcp_settings,
    )


def _run_bootstrap_blocking() -> None:
    """Synchronous bootstrap body — runs on a worker thread so the
    FastAPI lifespan can `await` it without blocking the event loop.
    """
    log = logging.getLogger("climasense_ml.bootstrap")

    _bootstrap.mark("in_progress", detail="probing SensorReadings row count")
    try:
        svc = _build_ingestion_service()
        result = svc.bootstrap_from_csv_if_empty()

        if result.skipped:
            _bootstrap.mark(
                "skipped",
                detail=(
                    f"SensorReadings already populated "
                    f"(probe returned {result.row_count_at_start})"
                ),
                rows_loaded=result.row_count_at_start,
            )
            log.info(
                "Bootstrap: skipped (table non-empty, probe %d)",
                result.row_count_at_start,
            )
            return

        _bootstrap.mark(
            "complete",
            detail=(
                f"bcp loaded {result.deduped_rows} deduped rows "
                f"(from {result.raw_rows} raw)"
            ),
            rows_loaded=result.deduped_rows,
        )
        log.info(
            "Bootstrap: complete (%d deduped, %d raw)",
            result.deduped_rows,
            result.raw_rows,
        )
    except BcpUnavailableError as ex:
        _bootstrap.mark("failed", detail=f"bcp unavailable: {ex}")
        log.exception("Bootstrap: failed (bcp unavailable)")
    except FileNotFoundError as ex:
        _bootstrap.mark("failed", detail=f"csv missing: {ex}")
        log.exception("Bootstrap: failed (csv missing)")
    except Exception as ex:  # noqa: BLE001 — record and surface
        _bootstrap.mark("failed", detail=f"{type(ex).__name__}: {ex}")
        log.exception("Bootstrap: failed (unexpected)")


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
    log.info("ClimaSense.ML starting (slice 3: bootstrap + stubs)")

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

    # Bootstrap pipeline — slice 3. Off-loaded to a worker thread so the
    # event loop stays responsive (the HTTP server can answer
    # /api/health/live the moment lifespan yields, while /api/health/ready
    # observes the bootstrap state through `_BootstrapTracker`).
    skip_bootstrap = os.environ.get(
        "CLIMASENSE_SKIP_BOOTSTRAP", ""
    ).lower() in ("1", "true", "yes")
    if skip_bootstrap:
        log.warning("Bootstrap: SKIPPED (CLIMASENSE_SKIP_BOOTSTRAP)")
        _bootstrap.mark(
            "skipped",
            detail="bootstrap skipped via CLIMASENSE_SKIP_BOOTSTRAP",
            rows_loaded=0,
        )
    else:
        log.info("Bootstrap: scheduling background task")
        # `asyncio.to_thread` runs `_run_bootstrap_blocking` on the
        # default executor. We fire-and-forget — the readiness probe
        # observes the outcome through `_bootstrap`. If the bootstrap
        # raises an unhandled exception, the wrapper inside
        # `_run_bootstrap_blocking` records it as state="failed".
        app.state.bootstrap_task = asyncio.create_task(
            asyncio.to_thread(_run_bootstrap_blocking)
        )

    yield

    log.info("ClimaSense.ML stopping")
    # Best-effort wait for bootstrap to finish on shutdown so the
    # lifespan logs are tidy. Real cancellation isn't necessary because
    # the bootstrap is short (30-90 s) and shutting down mid-run leaves
    # the DB in the same idempotent state we started with.
    task = getattr(app.state, "bootstrap_task", None)
    if task is not None and not task.done():
        task.cancel()
        try:
            await task
        except (asyncio.CancelledError, Exception):  # noqa: BLE001
            pass


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
    clock: IClock, checks: dict[str, str], ready: bool
) -> dict[str, object]:
    """Compose a body shaped exactly like `HealthStatus`.

    We construct the dict (rather than a Pydantic instance) so the
    `service` and `status` fields use the camelCase wire spelling
    without any aliasing surprises — the response is hand-rolled JSON
    while Pydantic owns the *schema* in `schemas/generated.py`.

    `ready` here means "every required check passes" — for slice 3
    that is DB reachable AND bootstrap complete (or skipped).
    """

    status_value: str
    if ready:
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
    """Readiness probe — DB reachable AND bootstrap complete/skipped.

    Slice 3 contract: returns 503 with `bootstrap=in_progress` while
    the bcp load is running, then flips to 200 with `bootstrap=complete`
    (or `bootstrap=skipped` on idempotent re-runs). The compose stack's
    web service `depends_on: ml: condition: service_healthy` uses this
    to gate dashboard availability — reviewers never see a populated
    dashboard before the rows are in.
    """
    ready, checks = await _probe_ready()
    body = _build_health_body(clock, checks, ready)
    code = status.HTTP_200_OK if ready else status.HTTP_503_SERVICE_UNAVAILABLE
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
    ready, checks = await _probe_ready()
    body = _build_health_body(clock, checks, ready)
    return JSONResponse(body, status_code=status.HTTP_200_OK)


async def _probe_ready() -> tuple[bool, dict[str, str]]:
    """Compose the readiness checks: DB + bootstrap.

    The pair `(ready, checks)` mirrors the slice-1 contract. `checks`
    keys are the per-dependency outcomes (`db`, `bootstrap`) so the
    body's `checks` map gives a reviewer running curl an unambiguous
    "what's holding us back" answer.
    """

    db_ok, checks = await _probe_db()
    bootstrap_ok = _probe_bootstrap(checks)
    return (db_ok and bootstrap_ok), checks


def _probe_bootstrap(checks: dict[str, str]) -> bool:
    """Map the bootstrap state to a readiness check.

    "complete" / "skipped" both gate-open the dashboard — slice 3 only
    cares that `SensorReadings` is non-empty.
    """
    state = _bootstrap.state
    if state in ("complete", "skipped"):
        checks["bootstrap"] = "ok"
        return True
    if state == "failed":
        checks["bootstrap"] = "fail"
        return False
    # "pending" or "in_progress"
    checks["bootstrap"] = "skipped"
    return False


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
