"""501-Not-Implemented stub handlers for the remaining contract surface.

Slice 2 stubbed every contract endpoint. Slice 5 lands the real
`/api/forecast` GET + POST handlers (see `forecast_router.py`). Slice 7
(#9) lands the real `/api/comfort/score` handler (see
`comfort_router.py`). Slice 8 (#10) lands the real
`/api/anomalies/detect` handler (see `anomaly_router.py`). The
remaining stub (profiles) continues to return 501 until slice 10 lands.

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

from fastapi import APIRouter, Body, Request, status
from fastapi.responses import JSONResponse

from .schemas import (
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


# NOTE: POST /api/anomalies/detect was a slice-2 stub; slice 8 (#10)
# promotes it to a real handler in `anomaly_router.py`. The route is
# registered on the FastAPI app before this stub router, so this
# module is now silent on anomalies.


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


# NOTE: GET /api/comfort/score was a slice-2 stub; slice 7 promotes
# it to a real handler in `comfort_router.py`. The route is registered
# on the FastAPI app before this stub router, so this module is now
# silent on comfort.
