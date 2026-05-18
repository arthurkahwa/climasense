"""FastAPI router for the real `/api/comfort/score` endpoint (slice 7).

Replaces the slice-2 stub that returned 501. The endpoint:

  * Pulls the trailing `hours` of `SensorReadings` clipped at the
    cursor.
  * Averages temperature and humidity.
  * Calls the pure `ComfortCalculator.score()` to produce a score,
    rating, and season.
  * Returns a `ComfortScoreResponse` envelope.

`GET` and the on-demand `POST /api/ml/run/comfort` proxy (web-tier)
both route here. The pure calculator + the trailing-window mean
together produce a deterministic envelope given the cursor's
position.

The endpoint does NOT write to `dbo.ComfortScores` — that's the
scheduler's job (β-prime gate). The on-demand semantics: "give me the
score for the current cursor's trailing window" — recomputed every
call, never persisted. Idempotent on the cursor.
"""

import logging
from typing import Annotated

from fastapi import APIRouter, Depends, Query, Request, status
from fastapi.responses import JSONResponse

from .comfort import Hemisphere
from .comfort_emitter import emit_comfort
from .cursor import CursorSnapshot
from .schemas import ComfortScoreResponse, ProblemDetails

log = logging.getLogger("climasense_ml.comfort_router")


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


def build_router(
    *,
    get_engine,  # type: ignore[no-untyped-def]
    get_cursor,  # type: ignore[no-untyped-def]
    get_hemisphere,  # type: ignore[no-untyped-def]
) -> APIRouter:
    """Construct the router with explicit dependency callables.

    `get_engine`, `get_cursor`, `get_hemisphere` are dependency
    callables returning the SQLAlchemy engine, the per-request
    `CursorSnapshot`, and the configured hemisphere respectively.
    Parameters so tests can swap fakes without monkeypatching.
    """

    router = APIRouter()

    @router.get(
        "/api/comfort/score",
        operation_id="getComfortScore",
        tags=["comfort"],
        responses={
            200: {"model": ComfortScoreResponse},
            503: {"model": ProblemDetails},
        },
    )
    async def get_comfort_score(
        request: Request,
        snap: Annotated[CursorSnapshot, Depends(get_cursor)],
        hours: Annotated[int, Query(ge=1, le=168)] = 24,
    ) -> JSONResponse:
        """Score the trailing `hours` of (T, RH) at the cursor.

        Per issue #9 AC ("POST /api/ml/run/comfort recomputes scores
        at the current cursor position; reruns are idempotent on
        (BucketTime)"), this endpoint **also persists** the score via
        `upsert_comfort_score`. The MERGE on `BucketTime` makes the
        write idempotent — replaying the same cursor yields the same
        row.

        Returns 200 with the comfort envelope, or 503 if the trailing
        window had zero readings (typically only happens during the
        brief bootstrap window before `SensorReadings` is populated).
        """
        engine = get_engine()
        hemisphere = get_hemisphere()
        emission = emit_comfort(
            engine,
            snap,
            hemisphere=hemisphere,
            window_hours=hours,
        )
        if emission is None:
            return _problem(
                request,
                code=status.HTTP_503_SERVICE_UNAVAILABLE,
                slug="empty_window",
                message=(
                    f"No SensorReadings rows in the last {hours} hours "
                    f"at cursor {snap.as_of.isoformat()}."
                ),
            )
        envelope = ComfortScoreResponse.model_validate(
            {
                "score": emission.result.score,
                "rating": emission.result.rating,
                "season": emission.result.season,
                "bucketTime": emission.bucket_time,
                "averageTemperature": emission.average_temperature,
                "averageHumidity": emission.average_humidity,
            }
        )
        return JSONResponse(
            status_code=status.HTTP_200_OK,
            content=envelope.model_dump(by_alias=True, mode="json"),
        )

    return router


__all__ = ["build_router"]
