"""FastAPI entry point for the ML tier.

Slice 5 surface (everything from slice 3 plus the live forecast):
  * GET  /api/health/live         — process up, no dependency check.
  * GET  /api/health/ready        — DB + bootstrap + forecaster fitted probe.
  * GET  /api/health              — combined alias (never 503; deps in `checks`).
  * GET  /api/forecast            — read latest forecast at cursor (slice 5).
  * POST /api/forecast            — boot-fit emission + persist (slice 5).
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
from .forecast_emitter import ForecastEmitter
from .forecast_router import build_router as build_forecast_router
from .forecaster import LagLinearForecaster, load_hourly_from_sql
from .ingestion import BcpUnavailableError, IngestionService
from .leaderboard import LeaderboardSeeder, SeedResult
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


# ---------------------------------------------------------------------
# Forecaster state machine (slice 5).
#
# Mirrors the bootstrap tracker. The forecaster is None until the
# lifespan boot-fit completes; readiness reports `forecaster=fail`
# while the fit hasn't run, `ok` after, `skipped` if disabled via
# CLIMASENSE_SKIP_FORECAST_FIT.
# ---------------------------------------------------------------------
ForecasterState = Literal["pending", "fitting", "ready", "failed", "skipped"]


class _ForecasterTracker:
    """Process-singleton holder for the boot-fit state. Thread-safe."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._state: ForecasterState = "pending"
        self._detail: str = "boot-fit has not been attempted yet"
        self._forecaster: LagLinearForecaster | None = None

    @property
    def state(self) -> ForecasterState:
        with self._lock:
            return self._state

    @property
    def detail(self) -> str:
        with self._lock:
            return self._detail

    @property
    def forecaster(self) -> LagLinearForecaster | None:
        with self._lock:
            return self._forecaster

    def mark(
        self,
        state: ForecasterState,
        *,
        detail: str,
        forecaster: LagLinearForecaster | None = None,
    ) -> None:
        with self._lock:
            self._state = state
            self._detail = detail
            if forecaster is not None:
                self._forecaster = forecaster


_forecaster_tracker = _ForecasterTracker()


def get_forecaster_state() -> _ForecasterTracker:
    """Public accessor — used by the readiness probe + forecast router."""
    return _forecaster_tracker


def get_forecaster() -> LagLinearForecaster | None:
    """Dependency callable for `forecast_router.build_router`."""
    return _forecaster_tracker.forecaster


# ---------------------------------------------------------------------
# Leaderboard seeder state machine (slice 6).
#
# Mirrors the bootstrap and forecaster trackers. The seeder runs once
# per lifespan, after the boot-fit completes (the live row needs a
# fitted forecaster). States:
#
#   * "pending"  — lifespan hasn't reached the seeder yet.
#   * "seeding"  — the MERGE pipeline is running.
#   * "complete" — both notebook + live rows are MERGEd successfully.
#   * "failed"   — any exception during seeding (DB error, missing
#                  results.json, parse failure). Captured for the
#                  readiness probe; the seeder does NOT block readiness
#                  because the leaderboard is a UI concern, not a wire
#                  contract concern. The readiness probe reports
#                  `leaderboard=fail` and FastAPI still gate-opens.
#   * "skipped"  — disabled via CLIMASENSE_SKIP_LEADERBOARD_SEED.
# ---------------------------------------------------------------------
LeaderboardSeedState = Literal["pending", "seeding", "complete", "failed", "skipped"]


class _LeaderboardTracker:
    """Process-singleton holder for leaderboard-seeding state. Thread-safe."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._state: LeaderboardSeedState = "pending"
        self._detail: str = "leaderboard seed has not been attempted yet"
        self._result: SeedResult | None = None

    @property
    def state(self) -> LeaderboardSeedState:
        with self._lock:
            return self._state

    @property
    def detail(self) -> str:
        with self._lock:
            return self._detail

    @property
    def result(self) -> SeedResult | None:
        with self._lock:
            return self._result

    def mark(
        self,
        state: LeaderboardSeedState,
        *,
        detail: str,
        result: SeedResult | None = None,
    ) -> None:
        with self._lock:
            self._state = state
            self._detail = detail
            if result is not None:
                self._result = result


_leaderboard_tracker = _LeaderboardTracker()


def get_leaderboard_state() -> _LeaderboardTracker:
    """Public accessor — used by the readiness probe and tests."""
    return _leaderboard_tracker


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


def _resolve_results_json() -> pathlib.Path:
    """Find `assets/results.json` for the leaderboard seeder.

    Mirrors `contract_validator._resolve_contract_path`: honour the
    env override first, then walk up from this module looking for
    `assets/results.json`. The container layout puts the file at
    `/app/assets/results.json` (copied in the Dockerfile); the
    developer layout puts it at repo-root.
    """
    override = os.environ.get("CLIMASENSE_RESULTS_JSON")
    if override:
        return pathlib.Path(override)
    here = pathlib.Path(__file__).resolve()
    for ancestor in [here, *here.parents]:
        candidate = ancestor / "assets" / "results.json"
        if candidate.is_file():
            return candidate
    # Fallback to the container layout even if not present yet — the
    # caller will surface a clean FileNotFoundError.
    return pathlib.Path("/app/assets/results.json")


def _run_leaderboard_seed_blocking() -> None:
    """Synchronous leaderboard-seeder body — runs on a worker thread.

    Reads `assets/results.json` and the boot-fitted forecaster, MERGEs
    the rows into `dbo.Leaderboard`. Idempotent on re-run. Records
    failure states so the readiness probe surfaces them without leaking
    a traceback.
    """
    log = logging.getLogger("climasense_ml.leaderboard")
    forecaster = _forecaster_tracker.forecaster
    if forecaster is None or not forecaster.fitted:
        _leaderboard_tracker.mark(
            "failed",
            detail="forecaster not fitted; cannot evaluate live row",
        )
        log.error(
            "LeaderboardSeeder: forecaster not fitted; refusing to seed"
        )
        return

    _leaderboard_tracker.mark("seeding", detail="merging notebook + live rows")
    try:
        results_path = _resolve_results_json()
        engine = get_engine()
        seeder = LeaderboardSeeder(
            engine=engine,
            results_json_path=results_path,
            forecaster=forecaster,
            history_loader=lambda: load_hourly_from_sql(engine),
        )
        result = seeder.run()
        _leaderboard_tracker.mark(
            "complete",
            detail=(
                f"merged {result.notebook_count} notebook + {result.live_count} "
                f"live rows ({result.changed_count} changed)"
            ),
            result=result,
        )
    except FileNotFoundError as ex:
        _leaderboard_tracker.mark("failed", detail=f"results.json missing: {ex}")
        log.exception("LeaderboardSeeder: failed (results.json missing)")
    except Exception as ex:  # noqa: BLE001 — record and surface
        _leaderboard_tracker.mark("failed", detail=f"{type(ex).__name__}: {ex}")
        log.exception("LeaderboardSeeder: failed (unexpected)")


def _run_boot_fit_blocking() -> None:
    """Synchronous lag-LR boot-fit body — runs on a worker thread so
    the lifespan can `await` it without blocking the event loop.

    Pulls the hourly history from `SensorReadings` via
    `load_hourly_from_sql`, fits the lag-LR coefficients, and stashes
    the fitted forecaster in `_forecaster_tracker`. Records failure
    states so the readiness probe surfaces them without leaking a
    traceback.
    """

    log = logging.getLogger("climasense_ml.boot_fit")
    _forecaster_tracker.mark("fitting", detail="loading hourly history from SensorReadings")

    try:
        engine = get_engine()
        history = load_hourly_from_sql(engine)
        if history.empty:
            _forecaster_tracker.mark(
                "failed",
                detail="SensorReadings was empty when boot-fit ran",
            )
            log.error("LagLinearForecaster: SensorReadings was empty; boot-fit aborted")
            return

        forecaster = LagLinearForecaster()
        summary = forecaster.fit_at_startup(history)
        _forecaster_tracker.mark(
            "ready",
            detail=(
                f"fit complete (n_train={summary.n_train}, "
                f"MAE={summary.mae:.4f}, RMSE={summary.rmse:.4f})"
            ),
            forecaster=forecaster,
        )
    except Exception as ex:  # noqa: BLE001 — record and surface
        _forecaster_tracker.mark("failed", detail=f"{type(ex).__name__}: {ex}")
        log.exception("LagLinearForecaster: boot-fit failed")


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

    # ----------------------------------------------------------------
    # Lag-LR boot-fit (slice 5). Chained after the bootstrap task so
    # the forecaster waits until `SensorReadings` is populated. The
    # APScheduler emission job is registered once the fit completes.
    # ----------------------------------------------------------------
    skip_fit = os.environ.get(
        "CLIMASENSE_SKIP_FORECAST_FIT", ""
    ).lower() in ("1", "true", "yes")

    if skip_fit:
        log.warning(
            "LagLinearForecaster: SKIPPED (CLIMASENSE_SKIP_FORECAST_FIT)"
        )
        _forecaster_tracker.mark(
            "skipped",
            detail="boot-fit skipped via CLIMASENSE_SKIP_FORECAST_FIT",
        )
    else:
        log.info("LagLinearForecaster: scheduling boot-fit (waits for bootstrap)")
        app.state.boot_fit_task = asyncio.create_task(
            _await_bootstrap_then_fit(app)
        )

    yield

    log.info("ClimaSense.ML stopping")
    # Best-effort cancellation. The bootstrap is short (30-90 s); the
    # boot-fit is sub-second. Cancelling mid-run leaves the DB in the
    # same idempotent state we started with.
    for attr in ("bootstrap_task", "boot_fit_task"):
        task = getattr(app.state, attr, None)
        if task is not None and not task.done():
            task.cancel()
            try:
                await task
            except (asyncio.CancelledError, Exception):  # noqa: BLE001
                pass
    sched = getattr(app.state, "forecast_scheduler", None)
    if sched is not None:
        try:
            sched.shutdown(wait=False)
        except Exception:  # noqa: BLE001
            log.exception("Forecast scheduler: shutdown raised")


async def _await_bootstrap_then_fit(app: FastAPI) -> None:
    """Wait for bootstrap to finish, then trigger the boot-fit.

    Polls `_bootstrap.state` instead of `await`-ing a specific task to
    keep this resilient to either ordering — the bootstrap may be
    `skipped` immediately (idempotent re-run) or take 30-90 s on first
    boot.
    """
    log = logging.getLogger("climasense_ml.boot_fit")
    while _bootstrap.state in ("pending", "in_progress"):
        await asyncio.sleep(0.5)

    if _bootstrap.state == "failed":
        _forecaster_tracker.mark(
            "failed",
            detail=f"bootstrap failed: {_bootstrap.detail}",
        )
        log.error(
            "LagLinearForecaster: boot-fit cannot run because bootstrap failed (%s)",
            _bootstrap.detail,
        )
        return

    # `complete` or `skipped` — both mean SensorReadings is populated.
    await asyncio.to_thread(_run_boot_fit_blocking)

    if _forecaster_tracker.state != "ready":
        log.warning(
            "Forecast scheduler: not registered (forecaster state=%s)",
            _forecaster_tracker.state,
        )
        # Leaderboard seeder also depends on a fitted forecaster; mark
        # the seeder failed so the readiness probe surfaces "why".
        _leaderboard_tracker.mark(
            "failed",
            detail=(
                f"skipped because forecaster state is "
                f"{_forecaster_tracker.state!r}"
            ),
        )
        return

    # ----------------------------------------------------------------
    # Leaderboard seed (slice 6). Runs after the boot-fit completes so
    # the live row's MAE/RMSE come from the same fitted forecaster the
    # scheduler will later use. Idempotent on re-run — a process
    # restart re-runs the MERGE which produces zero net changes.
    # ----------------------------------------------------------------
    skip_seed = os.environ.get(
        "CLIMASENSE_SKIP_LEADERBOARD_SEED", ""
    ).lower() in ("1", "true", "yes")
    if skip_seed:
        log.info(
            "LeaderboardSeeder: SKIPPED (CLIMASENSE_SKIP_LEADERBOARD_SEED)"
        )
        _leaderboard_tracker.mark(
            "skipped",
            detail="leaderboard seed skipped via env override",
        )
    else:
        log.info("LeaderboardSeeder: scheduling seed (after boot-fit)")
        await asyncio.to_thread(_run_leaderboard_seed_blocking)

    skip_scheduler = os.environ.get(
        "CLIMASENSE_SKIP_FORECAST_SCHEDULER", ""
    ).lower() in ("1", "true", "yes")
    if skip_scheduler:
        log.info(
            "Forecast scheduler: SKIPPED (CLIMASENSE_SKIP_FORECAST_SCHEDULER)"
        )
        return
    try:
        _register_forecast_scheduler(app)
    except Exception:  # noqa: BLE001
        log.exception("Forecast scheduler: registration failed")


def _register_forecast_scheduler(app: FastAPI) -> None:
    """Register the APScheduler interval job that drives β-prime
    forecast emission once per replay-hour.

    The scheduler is started here and shut down by the lifespan
    teardown. The job uses `ForecastEmitter.emit_if_due` which gates
    internally on `CursorSnapshot.should_emit` — at wall-clock speed
    this means roughly one row per hour; under slice-12 `ReplayClock`
    (60×) it produces one row per replay-hour, i.e. ~one per wall-minute.
    """
    from apscheduler.schedulers.background import BackgroundScheduler

    log = logging.getLogger("climasense_ml.scheduler")
    forecaster = _forecaster_tracker.forecaster
    if forecaster is None:
        log.warning("Forecast scheduler: forecaster unavailable; skipping registration")
        return

    emitter = ForecastEmitter(
        forecaster=forecaster,
        engine=get_engine(),
        clock_provider=lambda: CursorSnapshot.from_clock(_clock),
        horizon_hours=72,
    )

    scheduler = BackgroundScheduler(daemon=True, timezone="UTC")
    # `next_run_time` is intentionally omitted so APScheduler schedules
    # the first run at `now() + interval`. Passing `None` would pause
    # the job; passing `datetime.utcnow()` would fire instantly which
    # races the forecaster fit. The 1-min lag is acceptable — the
    # on-demand POST endpoint covers the "give me one right now"
    # reviewer flow.
    scheduler.add_job(
        emitter.emit_if_due,
        trigger="interval",
        minutes=1,
        id="forecast-emit",
        max_instances=1,
        coalesce=True,
    )
    scheduler.start()
    app.state.forecast_scheduler = scheduler
    app.state.forecast_emitter = emitter
    log.info("Forecast scheduler: started (interval=1 min, cadence=1 h)")


# ---------------------------------------------------------------------
# App
# ---------------------------------------------------------------------
app = FastAPI(
    title="ClimaSense.ML",
    version="0.6.0-slice-6",
    lifespan=lifespan,
)
app.add_middleware(CursorScopeMiddleware)
app.add_middleware(RequestIdMiddleware)

# Slice 5: real /api/forecast handlers (boot-fit emission). Registered
# BEFORE the stub router so the stub router only carries the still-
# pending endpoints (anomalies, profiles, comfort).
app.include_router(
    build_forecast_router(
        get_forecaster=get_forecaster,
        get_engine=get_engine,
        get_cursor=get_cursor,
    )
)

# Stub routes for the still-pending contract surface (anomalies /
# profiles / comfort). Each returns 501 with a `ProblemDetails` body.
# Slice 5 dropped /api/forecast (now real) from this router.
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
    """Compose the readiness checks: DB + bootstrap + forecaster + leaderboard.

    The pair `(ready, checks)` mirrors the slice-1 contract. `checks`
    keys are the per-dependency outcomes (`db`, `bootstrap`,
    `forecaster`, `leaderboard`) so the body's `checks` map gives a
    reviewer running curl an unambiguous "what's holding us back"
    answer.

    Slice 6 adds the `leaderboard` check as **observability only** —
    a failed seed is logged and surfaced in `checks` but does NOT
    flip readiness to 503. The leaderboard is a UI concern; the wire
    contract (forecast emission, range/heatmap reads) is unaffected
    by a seed failure.
    """

    db_ok, checks = await _probe_db()
    bootstrap_ok = _probe_bootstrap(checks)
    forecaster_ok = _probe_forecaster(checks)
    _probe_leaderboard(checks)  # observability only — return value ignored
    return (db_ok and bootstrap_ok and forecaster_ok), checks


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


def _probe_forecaster(checks: dict[str, str]) -> bool:
    """Map the lag-LR boot-fit state to a readiness check.

    `ready` and `skipped` (env override) gate-open. `failed` reports
    `fail`. `pending` / `fitting` report `skipped` (transient).
    """
    state = _forecaster_tracker.state
    if state in ("ready", "skipped"):
        checks["forecaster"] = "ok"
        return True
    if state == "failed":
        checks["forecaster"] = "fail"
        return False
    checks["forecaster"] = "skipped"
    return False


def _probe_leaderboard(checks: dict[str, str]) -> None:
    """Project the leaderboard-seeder state into the `checks` map.

    Observability-only — readiness does NOT depend on the seeder. The
    contract: `complete` / `skipped` → `ok`; `failed` → `fail`;
    `pending` / `seeding` → `skipped`.
    """
    state = _leaderboard_tracker.state
    if state in ("complete", "skipped"):
        checks["leaderboard"] = "ok"
    elif state == "failed":
        checks["leaderboard"] = "fail"
    else:
        checks["leaderboard"] = "skipped"


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
