# syntax=docker/dockerfile:1.7
#
# ClimaSense.ML — FastAPI placeholder for slice 1.
#
# We use the official `uv` image so the Python toolchain matches local
# dev. `unixodbc` + the Microsoft ODBC Driver 18 for SQL Server are
# installed so pyodbc can talk to the `db` service.

ARG PYTHON_VERSION=3.13

FROM ghcr.io/astral-sh/uv:python${PYTHON_VERSION}-bookworm-slim AS base

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_PROJECT_ENVIRONMENT=/opt/venv

# Microsoft ODBC Driver 18 for SQL Server.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        curl ca-certificates gnupg apt-transport-https \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
        | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && echo "deb [arch=amd64,arm64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/debian/12/prod bookworm main" \
        > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y --no-install-recommends \
        msodbcsql18 unixodbc unixodbc-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Install dependencies first so they cache independently of source changes.
COPY pyproject.toml ./
RUN uv venv /opt/venv \
    && uv pip install --python /opt/venv/bin/python --no-cache \
        "fastapi>=0.115,<0.120" \
        "uvicorn[standard]>=0.32,<0.40" \
        "python-json-logger>=2.0.7,<3" \
        "sqlalchemy>=2.0.36,<3" \
        "pyodbc>=5.2,<6" \
        "pydantic>=2.9,<3"

COPY climasense_ml ./climasense_ml

ENV PATH="/opt/venv/bin:$PATH"
EXPOSE 8000

CMD ["uvicorn", "climasense_ml.main:app", "--host", "0.0.0.0", "--port", "8000", "--log-config", "/dev/null"]
