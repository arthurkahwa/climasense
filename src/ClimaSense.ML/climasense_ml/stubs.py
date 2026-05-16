"""501-Not-Implemented stub handlers for the slice-2 contract surface.

Every endpoint declared in `contracts/openapi.yaml` has a Python
handler. The handlers in this module return 501 with a uniform
`ProblemDetails` body until the implementing slice (#7–#11) replaces
them with real handlers.

Why dedicated stubs (rather than a wildcard catch-all):

* FastAPI's auto-emitted OpenAPI only contains paths that have a
  decorated handler. Without the stubs the `ContractValidator` would
  flag every contract endpoint as "declared but not emitted" and fail
  startup. Stubs make the contract structurally enforceable.
* Stubs let us declare the camelCase request/response models — the
  emitted schemas then come straight from the Pydantic types in
  `schemas/generated.py`, which is what the contract validator
  compares against.
* The 501 body is itself a contract shape (`ProblemDetails`), so
  client callers can pattern-match on `error == "not_implemented"`
  without bespoke parsing.
"""

from __future__ import annotations

from typing import Annotated

from fastapi import APIRouter, Body, Query, Request, status
from fastapi.responses import JSONResponse

from .schemas import (
    AnomalyDetectRequest,
    AnomalyDetectResponse,
    ComfortScoreResponse,
    ForecastEnvelope,
    ForecastRequest,
    ProblemDetails,
    ProfilesAnalyzeRequest,
    ProfilesAnalyzeResponse,
)

router = APIRouter()


def _not_implemented(request: Request, slug: str, message: str) -> JSONResponse:
    """Compose a `ProblemDetails` body with the request-id echo.

    `ProblemDetails` is generated with `extra="forbid"` AND aliases
    (`requestId`) — populating via the camelCase alias name keeps the
    constructor happy without needing to flip `populate_by_name` on
    every generated model.
    """

    body = ProblemDetails.model_validate(
        {
            "error": slug,
            "message": message,
            "requestId": getattr(request.state, "request_id", None),
        }
    )
    return JSONResponse(
        status_code=status.HTTP_501_NOT_IMPLEMENTED,
        content=body.model_dump(by_alias=True, exclude_none=True),
    )


# -------------------------------------------------------------------
# Forecast
# -------------------------------------------------------------------
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
    horizon_hours: Annotated[int, Query(alias="horizonHours", ge=1, le=168)] = 72,
) -> JSONResponse:
    """Stubbed in slice 2; lag-LR boot-fit emission lands in slice 7."""
    del horizon_hours
    return _not_implemented(
        request,
        "not_implemented",
        "GET /api/forecast lands in slice 7 (lag-LR boot-fit emission).",
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
    body: Annotated[ForecastRequest, Body()],
) -> JSONResponse:
    """Stubbed in slice 2; emission engine lands in slice 7."""
    del body
    return _not_implemented(
        request,
        "not_implemented",
        "POST /api/forecast lands in slice 7 (lag-LR emission persistence).",
    )


# -------------------------------------------------------------------
# Anomalies
# -------------------------------------------------------------------
@router.post(
    "/api/anomalies/detect",
    operation_id="postAnomaliesDetect",
    tags=["anomalies"],
    responses={
        200: {"model": AnomalyDetectResponse},
        501: {"model": ProblemDetails},
        502: {"model": ProblemDetails},
        503: {"model": ProblemDetails},
        504: {"model": ProblemDetails},
    },
)
async def post_anomalies_detect(
    request: Request,
    body: Annotated[AnomalyDetectRequest, Body()],
) -> JSONResponse:
    """Stubbed in slice 2; three-detector pipeline lands in slice 9."""
    del body
    return _not_implemented(
        request,
        "not_implemented",
        "POST /api/anomalies/detect lands in slice 9 (three-detector pipeline).",
    )


# -------------------------------------------------------------------
# Profiles
# -------------------------------------------------------------------
@router.post(
    "/api/profiles/analyze",
    operation_id="postProfilesAnalyze",
    tags=["profiles"],
    responses={
        200: {"model": ProfilesAnalyzeResponse},
        501: {"model": ProblemDetails},
        502: {"model": ProblemDetails},
        503: {"model": ProblemDetails},
        504: {"model": ProblemDetails},
    },
)
async def post_profiles_analyze(
    request: Request,
    body: Annotated[ProfilesAnalyzeRequest, Body()],
) -> JSONResponse:
    """Stubbed in slice 2; calendar-conditioned profiles land in slice 10."""
    del body
    return _not_implemented(
        request,
        "not_implemented",
        "POST /api/profiles/analyze lands in slice 10 (calendar-conditioned profiles).",
    )


# -------------------------------------------------------------------
# Comfort
# -------------------------------------------------------------------
@router.get(
    "/api/comfort/score",
    operation_id="getComfortScore",
    tags=["comfort"],
    responses={
        200: {"model": ComfortScoreResponse},
        501: {"model": ProblemDetails},
        502: {"model": ProblemDetails},
        503: {"model": ProblemDetails},
        504: {"model": ProblemDetails},
    },
)
async def get_comfort_score(
    request: Request,
    hours: Annotated[int, Query(ge=1, le=168)] = 24,
) -> JSONResponse:
    """Stubbed in slice 2; ASHRAE 55 polygon scoring lands in slice 8."""
    del hours
    return _not_implemented(
        request,
        "not_implemented",
        "GET /api/comfort/score lands in slice 8 (ASHRAE 55 graphical zone).",
    )
