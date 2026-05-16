"""IClock + WallClock for the Python tier.

Mirrors `src/ClimaSense.Web/Clock/IClock.cs`. Per ADR-0004 every
"now" call in this tier MUST go through this module — a fresh `datetime.now()`
or `datetime.utcnow()` anywhere else is a slice-12 (ReplayClock) bug.

Slice 1 only ships `WallClock`. The registration site in
`climasense_ml.main` carries a `# TODO(slice-12)` comment marking the
point where `ReplayClock` will be selected via `CLIMASENSE_CLOCK_MODE`.
"""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Protocol


class IClock(Protocol):
    """Single source of "now". The only `datetime.now`-like call site
    in the tier outside this module is `WallClock.utc_now`."""

    def utc_now(self) -> datetime: ...


class WallClock:
    """Production-default `IClock` returning `datetime.now(tz=UTC)`.

    The ONLY place in the tier that calls `datetime.now()` directly.
    """

    def utc_now(self) -> datetime:
        return datetime.now(tz=timezone.utc)
