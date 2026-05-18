"""501-Not-Implemented stub handlers for the remaining contract surface.

Slice 2 stubbed every contract endpoint. Slice 5 lands the real
`/api/forecast` GET + POST handlers (see `forecast_router.py`). Slice 7
(#9) lands the real `/api/comfort/score` handler (see
`comfort_router.py`). Slice 8 (#10) lands the real
`/api/anomalies/detect` handler (see `anomaly_router.py`). Slice 9
(#11) lands the real `/api/profiles/analyze` handler (see
`profile_router.py`).

As of slice 9 the stub router is empty — every contract surface that
the ml tier owns has a real handler. The module is retained so that
adding a future slice 11+ endpoint can land a 501 stub here while the
real handler is being built, preserving the slice-2 contract-validator
discipline (every contract path has SOME emitted handler).

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

from fastapi import APIRouter

router = APIRouter()


# NOTE: POST /api/anomalies/detect was a slice-2 stub; slice 8 (#10)
# promotes it to a real handler in `anomaly_router.py`. The route is
# registered on the FastAPI app before this stub router, so this
# module is silent on anomalies.

# NOTE: POST /api/profiles/analyze was a slice-2 stub; slice 9 (#11)
# promotes it to a real handler in `profile_router.py`. The route is
# registered on the FastAPI app before this stub router, so this
# module is silent on profiles.

# NOTE: GET /api/comfort/score was a slice-2 stub; slice 7 promotes
# it to a real handler in `comfort_router.py`. The route is registered
# on the FastAPI app before this stub router, so this module is silent
# on comfort.
