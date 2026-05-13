# ClimaSense.ML

FastAPI + SQLAlchemy ML tier. Slice 1 ships only the scaffolding —
healthchecks, structured logging, request-ID propagation, the
`CursorSnapshot` value type, and the `IClock` abstraction.

## Local setup (uv)

```bash
uv venv
uv pip install -e ".[dev]"
uv run uvicorn climasense_ml.main:app --host 0.0.0.0 --port 8000
```

## Tests

```bash
uv run pytest
```
