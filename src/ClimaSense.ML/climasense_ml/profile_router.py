"""FastAPI router for the real `POST /api/profiles/analyze` endpoint (slice 9).

Replaces the slice-2 stub that returned 501. The endpoint:

  * Reads `{startDate, endDate}` from the request body.
  * Runs `profile_emitter.recompute_range` to compute the residuals,
    classify the Pattern via the SQL CASE function, and MERGE rows
    into `dbo.DayProfiles`.
  * Returns a `ProfilesAnalyzeResponse` envelope with the number of
    rows replaced and the rows themselves (re-read through the
    cursor-clipped TVF).

Idempotent on `[startDate, endDate]` — re-running yields zero net
changes (compute is deterministic; MERGE is keyed on Date; pattern
labels are SQL-side and deterministic too).
"""

from __future__ import annotations

import logging
from typing import Annotated

from fastapi import APIRouter, Body, Depends, Request, status
from fastapi.responses import JSONResponse

from .cursor import CursorSnapshot
from .profile_emitter import MAX_RANGE_DAYS, recompute_range
from .schemas import (
    DayProfileRow,
    Pattern,
    ProblemDetails,
    ProfilesAnalyzeRequest,
    ProfilesAnalyzeResponse,
)

log = logging.getLogger("climasense_ml.profile_router")


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
    history_loader=None,  # type: ignore[no-untyped-def]
) -> APIRouter:
    """Construct the router with explicit dependency callables.

    `history_loader` is a test seam: production passes `None` and the
    emitter loads from SQL via `forecaster.load_hourly_from_sql`.
    """

    router = APIRouter()

    cursor_dep = Depends(get_cursor)

    @router.post(
        "/api/profiles/analyze",
        operation_id="postProfilesAnalyze",
        tags=["profiles"],
        responses={
            200: {"model": ProfilesAnalyzeResponse},
            400: {"model": ProblemDetails},
            503: {"model": ProblemDetails},
        },
    )
    async def post_profiles_analyze(
        request: Request,
        body: Annotated[ProfilesAnalyzeRequest, Body()],
        snap: CursorSnapshot = cursor_dep,
    ) -> JSONResponse:
        """Recompute `DayProfiles` rows for `[startDate, endDate]`.

        400 on invalid range (start > end or span > MAX_RANGE_DAYS).
        503 when the underlying history is empty (typically only
        during the brief bootstrap window).
        """
        if body.start_date > body.end_date:
            return _problem(
                request,
                code=status.HTTP_400_BAD_REQUEST,
                slug="invalid_range",
                message=(
                    f"startDate ({body.start_date.isoformat()}) must be on or "
                    f"before endDate ({body.end_date.isoformat()})."
                ),
            )
        span_days = (body.end_date - body.start_date).days + 1
        if span_days > MAX_RANGE_DAYS:
            return _problem(
                request,
                code=status.HTTP_400_BAD_REQUEST,
                slug="range_too_large",
                message=(
                    f"requested window spans {span_days} days; cap is "
                    f"{MAX_RANGE_DAYS}."
                ),
            )

        engine = get_engine()
        try:
            result = recompute_range(
                engine,
                snap,
                start_date=body.start_date,
                end_date=body.end_date,
                history_loader=history_loader,
            )
        except ValueError as ex:
            return _problem(
                request,
                code=status.HTTP_400_BAD_REQUEST,
                slug="invalid_range",
                message=str(ex),
            )

        if result.rows_replaced == 0 and not result.rows:
            log.info(
                "post_profiles_analyze: empty result (window=[%s, %s])",
                body.start_date.isoformat(),
                body.end_date.isoformat(),
            )

        wire_rows = [
            DayProfileRow.model_validate(
                {
                    "date": r.date,
                    "dayOfWeek": int(r.day_of_week),
                    "meanResidual": float(r.mean_residual),
                    "maxAbsZscore": float(r.max_abs_zscore),
                    "pattern": Pattern(r.pattern),
                }
            )
            for r in result.rows
        ]
        envelope = ProfilesAnalyzeResponse.model_validate(
            {
                "rowsReplaced": int(result.rows_replaced),
                "rows": wire_rows,
            }
        )
        return JSONResponse(
            status_code=status.HTTP_200_OK,
            content=envelope.model_dump(by_alias=True, mode="json"),
        )

    return router


__all__ = ["build_router"]
