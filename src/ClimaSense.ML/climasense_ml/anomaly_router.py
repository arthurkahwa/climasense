"""FastAPI router for the real `/api/anomalies/detect` endpoint (slice 8).

Replaces the slice-2 stub that returned 501. The endpoint:

  * Constructs the three detectors from the engine + boot-fitted
    forecaster.
  * Runs the orchestrator (`run_all_detectors`) at the current cursor.
  * Reads back the rows that landed inside the run window through the
    cursor-clipped TVF (`dbo.fv_anomalies_at_cursor`).
  * Returns an `AnomalyDetectResponse` envelope: per-type counts, total
    inserted, total scanned, plus the rows that landed.

The endpoint is idempotent on the cursor's position — re-running at
the same cursor yields:

  * Zero new `sensor_failure` rows (`WHERE NOT EXISTS` gate).
  * Zero new `residual_outlier` rows (same gate).
  * A stable `regime_shift` row count (scan-and-replace with
    deterministic PELT).
"""

from __future__ import annotations

import logging
from datetime import timedelta
from typing import Annotated

from fastapi import APIRouter, Body, Depends, Request, status
from fastapi.responses import JSONResponse

from .anomaly_changepoint import (
    ChangepointDetector,
    DEFAULT_DAYS as CHANGEPOINT_DEFAULT_DAYS,
)
from .anomaly_orchestrator import (
    AnomalyRunSummary,
    run_all_detectors,
)
from .anomaly_persistence import read_recent_rows
from .anomaly_residual import (
    DEFAULT_Z_THRESHOLD,
    ResidualOutlierDetector,
    SCAN_WINDOW as RESIDUAL_SCAN_WINDOW,
)
from .anomaly_sensor_failure import (
    SCAN_WINDOW as SENSOR_FAILURE_SCAN_WINDOW,
    SensorFailureRules,
)
from .cursor import CursorSnapshot
from .schemas import (
    AnomalyDetectRequest,
    AnomalyDetectResponse,
    AnomalyRow,
    AnomalyRunSummary as WireAnomalyRunSummary,
    AnomalyType,
    ProblemDetails,
)

log = logging.getLogger("climasense_ml.anomaly_router")


def _problem(request: Request, code: int, slug: str, message: str) -> JSONResponse:
    body = ProblemDetails.model_validate(
        {
            "error": slug,
            "message": message,
            "requestId": getattr(request.state, "request_id", None),
        }
    )
    return JSONResponse(
        status_code=code,
        content=body.model_dump(by_alias=True, exclude_none=True),
    )


def _build_envelope(
    summary: AnomalyRunSummary,
    rows: list,  # type: ignore[type-arg]
) -> AnomalyDetectResponse:
    """Compose the wire envelope from the orchestrator summary + read rows."""

    wire_rows = [
        AnomalyRow.model_validate(
            {
                "anomalyType": r.anomaly_type,
                "readingTime": r.reading_time,
                "severity": float(r.severity),
                "description": r.description,
            }
        )
        for r in rows
    ]
    return AnomalyDetectResponse.model_validate(
        {
            "inserted": summary.total_inserted,
            "totalScanned": summary.total_scanned,
            "perType": WireAnomalyRunSummary.model_validate(
                {
                    "sensorFailure": summary.sensor_failure,
                    "residualOutlier": summary.residual_outlier,
                    "regimeShift": summary.regime_shift,
                }
            ),
            "rows": wire_rows,
        }
    )


def build_router(
    *,
    get_engine,  # type: ignore[no-untyped-def]
    get_cursor,  # type: ignore[no-untyped-def]
    get_forecaster,  # type: ignore[no-untyped-def]
    sensor_failure_factory=None,  # type: ignore[no-untyped-def]
    residual_outlier_factory=None,  # type: ignore[no-untyped-def]
    changepoint_factory=None,  # type: ignore[no-untyped-def]
) -> APIRouter:
    """Construct the router with explicit dependency callables.

    The three `*_factory` parameters are test seams. Production passes
    `None` and the router constructs the detectors itself from the
    engine + boot-fitted forecaster.
    """

    router = APIRouter()

    # Capture the dependency callable in a name FastAPI's introspection
    # can resolve. `Depends(closure_var)` is the canonical pattern used
    # by `comfort_router.build_router` (slice 7).
    cursor_dep = Depends(get_cursor)

    @router.post(
        "/api/anomalies/detect",
        operation_id="postAnomaliesDetect",
        tags=["anomalies"],
        responses={
            200: {"model": AnomalyDetectResponse},
            503: {"model": ProblemDetails},
        },
    )
    async def post_anomalies_detect(
        request: Request,
        body: Annotated[AnomalyDetectRequest, Body()],
        snap: CursorSnapshot = cursor_dep,
    ) -> JSONResponse:
        """Run the three-detector pipeline at the current cursor.

        `body.types` selects which detectors to run; defaults to all
        three when the field would otherwise be empty (the contract
        enforces `minItems: 1`). The on-demand button passes all three.
        """

        engine = get_engine()
        forecaster = get_forecaster()
        if forecaster is None or not forecaster.fitted:
            return _problem(
                request,
                code=status.HTTP_503_SERVICE_UNAVAILABLE,
                slug="forecaster_not_ready",
                message=(
                    "ResidualOutlierDetector requires a boot-fitted "
                    "LagLinearForecaster. The forecaster has not "
                    "completed its boot-fit yet (or boot-fit failed). "
                    "Wait for /api/health/ready to report `forecaster=ok`."
                ),
            )

        sensor_failure_rules = (
            sensor_failure_factory(engine)
            if sensor_failure_factory is not None
            else SensorFailureRules(engine=engine)
        )
        residual_outlier_detector = (
            residual_outlier_factory(engine, forecaster)
            if residual_outlier_factory is not None
            else ResidualOutlierDetector(engine=engine, forecaster=forecaster)
        )
        changepoint_detector = (
            changepoint_factory(engine)
            if changepoint_factory is not None
            else ChangepointDetector(engine=engine)
        )

        summary = run_all_detectors(
            snap,
            sensor_failure_rules=sensor_failure_rules,
            residual_outlier_detector=residual_outlier_detector,
            changepoint_detector=changepoint_detector,
        )

        # Read back the rows that landed in *any* of the three windows.
        # The widest window is the changepoint scan (90 d); we use it
        # so the response surfaces every typed row visible at the cursor.
        since = snap.as_of - timedelta(days=CHANGEPOINT_DEFAULT_DAYS)
        rows = read_recent_rows(engine, snap=snap, since=since)

        envelope = _build_envelope(summary, rows)
        return JSONResponse(
            status_code=status.HTTP_200_OK,
            content=envelope.model_dump(by_alias=True, mode="json"),
        )

    return router


__all__ = ["build_router"]
