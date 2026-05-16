"""SQLAlchemy engine factory.

Slice 1 only uses the engine for the `/api/health/ready` probe. Later
slices will use `autoload_with=engine` to reflect the schema authored
in `scripts/init-db.sql` — there is no `Base.metadata.create_all()`
in this codebase.
"""

from __future__ import annotations

import os
from functools import lru_cache
from urllib.parse import quote_plus

from sqlalchemy import Engine, create_engine


def _connection_url() -> str:
    host = os.environ.get("CLIMASENSE_DB_HOST", "db")
    port = os.environ.get("CLIMASENSE_DB_PORT", "1433")
    name = os.environ.get("CLIMASENSE_DB_NAME", "ClimaSense")
    user = os.environ.get("CLIMASENSE_DB_USER", "sa")
    pwd = os.environ.get("CLIMASENSE_DB_PASSWORD", "")

    # ODBC Driver 18 for SQL Server is installed via the Dockerfile.
    odbc = (
        f"DRIVER={{ODBC Driver 18 for SQL Server}};"
        f"SERVER={host},{port};"
        f"DATABASE={name};"
        f"UID={user};"
        f"PWD={pwd};"
        f"Encrypt=yes;TrustServerCertificate=yes;"
    )
    return f"mssql+pyodbc:///?odbc_connect={quote_plus(odbc)}"


@lru_cache(maxsize=1)
def get_engine() -> Engine:
    """Lazy singleton engine. `lru_cache` is enough — uvicorn imports
    once per process."""
    return create_engine(
        _connection_url(),
        pool_pre_ping=True,
        pool_size=5,
        max_overflow=5,
        future=True,
    )
