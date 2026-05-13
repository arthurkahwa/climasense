"""CursorSnapshot — the Python mirror of
`src/ClimaSense.Web/Cursor/CursorSnapshot.cs`.

Per CONTEXT.md:

* Immutable. Constructed once per logical operation.
* `as_of` — the frozen cursor value (UTC-aware `datetime`).
* `clip(query)` — appends `WHERE ReadingTime <= :as_of` to a SQL string
  targeting the *raw* `SensorReadings` table only. Derived tables
  (Forecasts, Anomalies, DayProfiles, ComfortScores, Alerts) clip via
  the inline TVFs `dbo.fv_<table>_at_cursor(@asOf)` defined in
  `scripts/init-db.sql`.
* `windowed(duration)` — returns `(start, end) = (as_of - duration, as_of)`.
* `should_emit(last_emit, cadence)` — β-prime emission gate.

Lifetime: bound via `contextvars` at request / job entry. FastAPI's
dependency injection produces one snapshot per request via
`climasense_ml.main.get_cursor`.
"""

from __future__ import annotations

from contextvars import ContextVar
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

from .clock import IClock


@dataclass(frozen=True, slots=True)
class CursorSnapshot:
    """Immutable cursor capture; see module docstring for the full contract."""

    as_of: datetime

    def __post_init__(self) -> None:
        # Coerce naive datetimes to UTC. Using object.__setattr__ because
        # the dataclass is frozen.
        if self.as_of.tzinfo is None:
            object.__setattr__(self, "as_of", self.as_of.replace(tzinfo=timezone.utc))
        elif self.as_of.tzinfo != timezone.utc:
            object.__setattr__(self, "as_of", self.as_of.astimezone(timezone.utc))

    # -----------------------------------------------------------------
    # Construction helpers
    # -----------------------------------------------------------------
    @classmethod
    def from_clock(cls, clock: IClock) -> "CursorSnapshot":
        """Build a snapshot from the configured `IClock`."""
        return cls(as_of=clock.utc_now())

    # -----------------------------------------------------------------
    # Operations
    # -----------------------------------------------------------------
    def clip(self, query: str, parameter_name: str = "as_of") -> tuple[str, dict[str, datetime]]:
        """Append `WHERE ReadingTime <= :as_of` to a SQL query targeting
        the raw `SensorReadings` table.

        Returns `(modified_query, {parameter_name: as_of})` so the caller
        can hand both into a SQLAlchemy `text()` invocation.

        Note: this method is for the raw table only. Use the
        `dbo.fv_<table>_at_cursor` TVFs for derived tables.
        """
        if not query or not query.strip():
            raise ValueError("query must be a non-empty SQL string")
        if not parameter_name or not parameter_name.isidentifier():
            raise ValueError("parameter_name must be a valid SQL identifier")

        trimmed = query.rstrip()
        upper = trimmed.upper()
        connector = " AND " if " WHERE " in upper else " WHERE "
        out = f"{trimmed}{connector}ReadingTime <= :{parameter_name}"
        return out, {parameter_name: self.as_of}

    def windowed(self, duration: timedelta) -> tuple[datetime, datetime]:
        """Return `(start, end) = (as_of - duration, as_of)`.

        `duration` MUST be strictly positive.
        """
        if duration <= timedelta(0):
            raise ValueError("duration must be strictly positive")
        return (self.as_of - duration, self.as_of)

    def should_emit(self, last_emit: datetime | None, cadence: timedelta) -> bool:
        """Return `True` iff `as_of - last_emit >= cadence`.

        `None` for `last_emit` means "never emitted" — the gate opens.
        """
        if cadence <= timedelta(0):
            raise ValueError("cadence must be strictly positive")
        if last_emit is None:
            return True
        if last_emit.tzinfo is None:
            last_emit = last_emit.replace(tzinfo=timezone.utc)
        elif last_emit.tzinfo != timezone.utc:
            last_emit = last_emit.astimezone(timezone.utc)
        return (self.as_of - last_emit) >= cadence


# ---------------------------------------------------------------------
# Context-var binding. `get_current()` returns the snapshot bound for
# the active request / job / task. `bind()` is the discipline used by
# FastAPI middleware (slice 1) and APScheduler job-entry hooks (slice 8+).
# ---------------------------------------------------------------------
_current: ContextVar[CursorSnapshot | None] = ContextVar(
    "climasense_cursor_snapshot", default=None
)


def bind(snapshot: CursorSnapshot) -> object:
    """Bind `snapshot` as the active cursor for the current context.

    Returns the `Token` that `contextvars` produces; the caller should
    pass it to `release()` to restore the prior value (typically in a
    `finally` block).
    """
    return _current.set(snapshot)


def release(token: object) -> None:
    """Reset the bound cursor to its pre-bind value."""
    _current.reset(token)  # type: ignore[arg-type]


def get_current() -> CursorSnapshot | None:
    """Return the snapshot bound for the active context, or `None`."""
    return _current.get()
