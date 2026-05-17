"""FastAPI router for the real `/api/forecast` endpoints (slice 5).

Replaces the slice-2 stubs that returned 501. `GET` reads the most
recent forecast batch through `dbo.fv_forecasts_at_cursor(@asOf)`;
`POST` runs the lag-LR emission cycle, persists the rows, and returns
the envelope.

The router depends on:
  * The boot-fitted `LagLinearForecaster` (stashed on `app.state.forecaster`).
  * A SQLAlchemy `Engine` (returned by `db.get_engine()`).
  * The per-request `CursorSnapshot` (FastAPI `Depends(get_cursor)`
    from `main.py`).

When the forecaster is not yet fitted (still in lifespan), POST returns
503 with `error=forecaster_not_ready`. GET returns 200 with an empty
points list (the dashboard can render "no forecast yet").
"""

import logging
from typing import Annotated

from fastapi import APIRouter, Body, Depends, Query, Request, status
from fastapi.responses import JSONResponse

from .cursor import CursorSnapshot
from .forecast_emitter import emit_forecast
from .forecast_persistence import read_latest_forecast_at_cursor
from .forecaster import LagLinearForecaster
from .schemas import ForecastEnvelope, ForecastPoint, ForecastRequest, ProblemDetails

log = logging.getLogger("climasense_ml.forecast_router")


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


def _row_to_point(row) -> ForecastPoint:  # type: ignore[no-untyped-def]
    return ForecastPoint.model_validate(
        {
            "targetTime": row.target_time,
            "predictedTemperature": row.predicted_temperature,
            "predictedHumidity": row.predicted_humidity,
            "confidenceLowerTemp": (
                row.confidence_lower_temp
                if row.confidence_lower_temp is not None
                else row.predicted_temperature
            ),
            "confidenceUpperTemp": (
                row.confidence_upper_temp
                if row.confidence_upper_temp is not None
                else row.predicted_temperature
            ),
        }
    )


def build_router(
    *,
    get_forecaster,  # type: ignore[no-untyped-def]
    get_engine,  # type: ignore[no-untyped-def]
    get_cursor,  # type: ignore[no-untyped-def]
) -> APIRouter:
    """Construct the router with explicit dependency callables.

    `get_forecaster`, `get_engine`, `get_cursor` are FastAPI deps
    returning the boot-fitted forecaster, the SQLAlchemy engine, and
    the per-request `CursorSnapshot` respectively. They're parameters
    so tests can swap fakes without monkeypatching.
    """

    router = APIRouter()

    @router.get(
        "/api/forecast",
        operation_id="getForecast",
        tags=["forecast"],
        responses={
            200: {"model": ForecastEnvelope},
            501: {"model": ProblemDetails},
        },
    )
    async def get_forecast(
        request: Request,
        snap: Annotated[CursorSnapshot, Depends(get_cursor)],
        horizon_hours: Annotated[int, Query(alias="horizonHours", ge=1, le=168)] = 72,
    ) -> JSONResponse:
        """Return the most recently emitted forecast at the cursor.

        Reads through `dbo.fv_forecasts_at_cursor(@asOf)` so cursor-
        clipping is enforced by the schema. Empty `points` is a valid
        response — happens before the first emission lands.
        """
        del horizon_hours  # honoured by POST; GET surfaces the latest persisted batch verbatim.
        engine = get_engine()
        rows = read_latest_forecast_at_cursor(engine, as_of=snap.as_of)
        if not rows:
            envelope = ForecastEnvelope.model_validate(
                {
                    "generatedAt": snap.as_of,
                    "modelVersion": "lag-lr-v1",
                    "horizonHours": 0,
                    "points": [],
                }
            )
            return JSONResponse(
                status_code=status.HTTP_200_OK,
                content=envelope.model_dump(by_alias=True, mode="json"),
            )
        envelope = ForecastEnvelope.model_validate(
            {
                "generatedAt": rows[0].generated_at,
                "modelVersion": rows[0].model_version,
                "horizonHours": len(rows),
                "points": [
                    _row_to_point(r).model_dump(by_alias=True, mode="json") for r in rows
                ],
            }
        )
        return JSONResponse(
            status_code=status.HTTP_200_OK,
            content=envelope.model_dump(by_alias=True, mode="json"),
        )

    @router.post(
        "/api/forecast",
        operation_id="postForecast",
        tags=["forecast"],
        responses={
            200: {"model": ForecastEnvelope},
            501: {"model": ProblemDetails},
            502: {"model": ProblemDetails},
            503: {"model": ProblemDetails},
            504: {"model": ProblemDetails},
        },
    )
    async def post_forecast(
        request: Request,
        snap: Annotated[CursorSnapshot, Depends(get_cursor)],
        body: Annotated[ForecastRequest, Body()],
    ) -> JSONResponse:
        """Emit a forecast at the current cursor and persist the rows.

        Loads the boot-fitted lag-LR coefficients; no per-request
        sklearn fit. Returns the `ForecastEnvelope` with `horizonHours`
        rows.
        """
        forecaster: LagLinearForecaster = get_forecaster()
        if forecaster is None or not forecaster.fitted:
            return _problem(
                request,
                code=status.HTTP_503_SERVICE_UNAVAILABLE,
                slug="forecaster_not_ready",
                message=(
                    "Lag-LR boot-fit has not completed. Wait for "
                    "`/api/health/ready` on the ml tier to return 200."
                ),
            )

        engine = get_engine()
        try:
            result = emit_forecast(
                forecaster,
                engine,
                snap,
                int(body.horizon_hours),
            )
        except RuntimeError as ex:
            log.warning("Forecast emission failed: %s", ex)
            return _problem(
                request,
                code=status.HTTP_503_SERVICE_UNAVAILABLE,
                slug="insufficient_history",
                message=str(ex),
            )

        envelope = ForecastEnvelope.model_validate(
            {
                "generatedAt": result.generated_at,
                "modelVersion": forecaster.model_version,
                "horizonHours": result.horizon_hours,
                "points": [
                    _row_to_point(r).model_dump(by_alias=True, mode="json")
                    for r in result.persisted_rows
                ],
            }
        )
        return JSONResponse(
            status_code=status.HTTP_200_OK,
            content=envelope.model_dump(by_alias=True, mode="json"),
        )

    return router


__all__ = ["build_router"]
