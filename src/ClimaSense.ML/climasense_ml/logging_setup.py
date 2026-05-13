"""Structured-JSON stdout logging for the ML tier.

Every log record carries `ts`, `level`, `service`, `msg`, and (when
present in context) `request_id`. This is the Python-side mirror of the
.NET `JsonStdoutFormatter`; both honour the same five required keys per
slice 1's acceptance criteria.

Implementation note: `python-json-logger` lets us add fixed fields and
inject context-bound fields without re-implementing the formatter.
"""

from __future__ import annotations

import logging
import os
import sys
from contextvars import ContextVar
from datetime import datetime, timezone
from typing import Any

from pythonjsonlogger import jsonlogger

SERVICE_NAME = "ml"
_request_id: ContextVar[str | None] = ContextVar("climasense_request_id", default=None)


def set_request_id(value: str | None) -> object:
    """Bind a request ID to the current context. Returns the reset token."""
    return _request_id.set(value)


def reset_request_id(token: object) -> None:
    _request_id.reset(token)  # type: ignore[arg-type]


def get_request_id() -> str | None:
    return _request_id.get()


class _ClimaSenseJsonFormatter(jsonlogger.JsonFormatter):
    """Subclass overriding `add_fields` so we can inject the timestamp,
    service name, and the per-context `request_id` deterministically."""

    def add_fields(
        self,
        log_record: dict[str, Any],
        record: logging.LogRecord,
        message_dict: dict[str, Any],
    ) -> None:
        super().add_fields(log_record, record, message_dict)

        # Standard required fields — always present.
        log_record.setdefault(
            "ts",
            datetime.fromtimestamp(record.created, tz=timezone.utc).isoformat(),
        )
        log_record.setdefault("level", record.levelname.lower())
        log_record.setdefault("service", SERVICE_NAME)
        log_record.setdefault("logger", record.name)
        log_record["msg"] = log_record.pop("message", record.getMessage())

        rid = _request_id.get()
        if rid:
            log_record.setdefault("request_id", rid)


def configure() -> None:
    """Install the JSON formatter on the root logger.

    Idempotent — calling twice does not duplicate handlers.
    """
    level_name = os.environ.get("LOG_LEVEL", "INFO").upper()
    level = getattr(logging, level_name, logging.INFO)

    root = logging.getLogger()
    root.setLevel(level)

    # Tear down whatever uvicorn / FastAPI installed so we have a single
    # stdout JSON handler.
    for handler in list(root.handlers):
        root.removeHandler(handler)

    handler = logging.StreamHandler(sys.stdout)
    handler.setLevel(level)
    handler.setFormatter(_ClimaSenseJsonFormatter())
    root.addHandler(handler)

    # Ensure uvicorn's own loggers route through the same handler instead
    # of double-formatting.
    for name in ("uvicorn", "uvicorn.error", "uvicorn.access", "fastapi"):
        lg = logging.getLogger(name)
        lg.handlers = []
        lg.propagate = True
        lg.setLevel(level)
