# Slice 2 implementation notes (OpenAPI contract + codegen)

> Auxiliary working-notes file for slice 2. Captures the reviewer-facing
> map of what landed and how to validate it. See PR body for the
> full acceptance-criteria evidence table.

## What landed

| Concern | Path | Notes |
|---|---|---|
| Extended contract | `contracts/openapi.yaml` | Five new endpoint groups (forecast / anomalies / profiles / comfort / combined health) plus eight new component schemas. camelCase wire spelling. Failure-mode documentation (503 / 502 / 504) in the `info.description` block. |
| Regen entry-point | `scripts/regen-contracts.sh` | `bash scripts/regen-contracts.sh [all\|python\|dotnet]`. Idempotent — re-running on unchanged contract produces zero diff. |
| Tool manifest | `.config/dotnet-tools.json` | Pins `microsoft.openapi.kiota` `1.31.1`. `dotnet tool restore` installs it. |
| .NET codegen | `src/ClimaSense.Web/Generated/MLClient/` | 32 files — 18 model DTOs + 12 request-builder hierarchy + 2 metadata. **No `[JsonPropertyName]` attributes anywhere.** |
| .NET runtime deps | `src/ClimaSense.Web/ClimaSense.Web.csproj` | Added `Microsoft.Kiota.Abstractions` (+ Http + 4 Serialization-*) pinned to `1.22.2`. |
| MSBuild regen target | `ClimaSense.Web.csproj` `<Target Name="KiotaRegenerate">` | `BeforeTargets="BeforeBuild"`. Shells out to `scripts/regen-contracts.sh dotnet`. Skipped with a warning if `dotnet kiota` is not restored. `-p:RegenerateKiotaClient=false` disables for cold-cache CI builds. |
| Python codegen | `src/ClimaSense.ML/climasense_ml/schemas/generated.py` | Pydantic v2 models with `Field(alias="camelCase")` so JSON round-trips match the contract. Banner-prepended by `regen-contracts.sh` so the file is visibly "generated". |
| Python pyproject | `src/ClimaSense.ML/pyproject.toml` | Added `PyYAML` (runtime — ContractValidator) and `datamodel-code-generator` (dev — codegen tool). |
| FastAPI Dockerfile | `src/ClimaSense.ML/Dockerfile` | Build context widened to repo-root so `contracts/openapi.yaml` is COPYable. Pydantic models regenerated at image-build time so the image always carries the freshly-generated DTOs. |
| ContractValidator | `src/ClimaSense.ML/climasense_ml/contract_validator.py` | Compares `app.openapi()` against the YAML on FastAPI startup. Logs `ContractValidator: OK (N paths, M schemas)` on match; logs `ContractValidator: FAILED — k delta(s)` then raises `ContractMismatchError` on mismatch (uvicorn exits non-zero). Filters FastAPI-only noise (`HTTPValidationError`, `ValidationError`, `/api/alerts/stream` — web-tier-only). |
| 501 stubs (Python) | `src/ClimaSense.ML/climasense_ml/stubs.py` | Every contract endpoint has a `@router` handler returning a `ProblemDetails` body with `error="not_implemented"`. Typed via the generated Pydantic DTOs so the OpenAPI emission matches the YAML. |
| .NET ↔ Python wire client | `src/ClimaSense.Web/ML/` | Hand-written `IMLServiceClient` + `MLServiceClient` (wraps Kiota's `MLApiClient`). |
| Failure-mapping handler | `MLFailureMappingHandler.cs` | `DelegatingHandler` converts socket-refused → `MLServiceUnavailableException`, upstream 5xx (excl. 501) → `MLServiceBadGatewayException`, timeout → `MLServiceTimeoutException`. 501 is passed through to Kiota's error mapping so the proxy reports "not_implemented" instead of "ml_service_error". |
| Request-ID propagation | `RequestIdPropagationHandler.cs` | Copies the inbound `X-Request-ID` from the current `HttpContext` onto every outbound ml-tier call. |
| Proxy endpoints | `MLProxyEndpoints.cs` (`MapMLProxy`) | Five endpoints under `/api/ml/*` exercise the failure mapping end-to-end so a reviewer's curl demonstrates AC #6 without contriving a test harness. |
| Tests (.NET) | `tests/ClimaSense.Web.Tests/` | 22 new tests across four files. Total .NET test count: **36 passing**. |
| Tests (Python) | `src/ClimaSense.ML/tests/` | 11 new tests across two files. Total Python test count: **29 passing**. |
| Notes | `SLICE-2-NOTES.md` | this file |
| ADR | `docs/adr/0015-contract-first-wire-format-and-codegen.md` | Pins the contract-first discipline + codegen tooling. |

## Verification (the same commands captured in the PR body)

```sh
# Static checks
python3 -m openapi_spec_validator contracts/openapi.yaml          # OK
python3 -m yaml -m yaml contracts/openapi.yaml                    # parses

# Codegen idempotence (must produce no diff)
bash scripts/regen-contracts.sh                                   # exits 0
git diff --stat src/ClimaSense.Web/Generated/MLClient/            # clean
git diff --stat src/ClimaSense.ML/climasense_ml/schemas/          # clean

# .NET
dotnet build -nologo                                              # regens Kiota client too
dotnet test --nologo --no-build                                   # 36 passed

# Python
python3 -m pytest src/ClimaSense.ML/tests/ -v                     # 29 passed

# Compose lifecycle
cp .env.example .env
docker compose up -d --wait --build                               # all 4 healthy

# Contract validator OK on the wire
docker compose logs ml | grep "ContractValidator: OK"             # 1 hit per start
# → "ContractValidator: OK (7 paths, 18 schemas)"

# Stub responses (501 with camelCase ProblemDetails)
docker exec climasense-ml curl -sS http://localhost:8000/api/forecast
# → {"error":"not_implemented","message":"GET /api/forecast lands in slice 7 ...","requestId":"..."}

# Proxy mapping — slice-2 stubs surface as 501 to the browser
docker exec climasense-web curl -sS -i http://localhost:8080/api/ml/forecast
# → HTTP/1.1 501 Not Implemented
# → {"error":"not_implemented", ...}

# Failure mapping — ml down → 503 with ml_service_unavailable to the browser
docker compose stop ml
docker exec climasense-web curl -sS -i http://localhost:8080/api/ml/forecast
# → HTTP/1.1 503 Service Unavailable
# → {"error":"ml_service_unavailable","message":"ml tier unreachable ...","requestId":"..."}

# ContractValidator fails fast — mutate YAML inside container, restart, expect exit code 3
docker exec climasense-ml python -c "import yaml,pathlib; p=pathlib.Path('/app/contracts/openapi.yaml'); s=yaml.safe_load(p.read_text()); s['paths']['/api/phantom']={'get':{'operationId':'x','responses':{'200':{'description':''}}}}; p.write_text(yaml.safe_dump(s))"
docker compose restart ml
docker compose ps --all | grep climasense-ml                      # → Exited (3)
docker compose logs ml | grep "ContractValidator: FAILED"
# → "ContractValidator: FAILED — 1 delta(s) ..."

# Reset
docker compose down -v
```

## What was deliberately NOT built (deferred to later slices)

* **Real handlers for the five stub endpoints** — slice 2 ships 501s only.
  Slice 7 lands `/api/forecast`. Slice 8: `/api/comfort/score`. Slice 9:
  `/api/anomalies/detect`. Slice 10: `/api/profiles/analyze`.
* **Request retries** — explicitly out of scope per issue #4. Slice 7+ may
  add Polly retries on read paths, but the slice-2 client is "one shot
  per call, bounded timeout, fail fast" per the contract.
* **EF Core scaffolding / DbContext** — slice 4 (#6).
* **Razor pages for the new endpoints** — slice 6 (#9).
* **Smoke test in CI** — slice 13 (#15).

## Judgment calls

1. **Generated tree IS committed.** AC #2 ("rerunning produces zero diff")
   strongly implies the snapshot is committed; otherwise there's nothing
   to diff against. Both Kiota's output and `datamodel-code-generator`'s
   output are committed. The `.gitignore` change is minimal — only
   `bin/`, `obj/`, `__pycache__/` (already ignored from slice 1) keep
   build noise out.

2. **Health endpoint surface is reconciled with the slice-1 hand-written
   shape via the generated schema, not by replacing the handlers.**
   The slice-1 `/api/health/live` and `/api/health/ready` returned
   anonymous JSON shaped like `HealthStatus`. Slice 2 keeps the
   anonymous-JSON construction at the call site (so reviewer diffs are
   small) but declares the response model as the generated `HealthStatus`
   so FastAPI's OpenAPI emission matches the contract. A separate
   `_build_health_body` helper formalises the wire shape — if anyone
   accidentally drops `service` or `status`, the ContractValidator
   catches it on startup. Equivalent of an integration test.

3. **`HealthStatus : ApiException` in the Kiota tree is accepted as
   harmless.** Kiota declares `HealthStatus` and `ProblemDetails` as
   `ApiException` subclasses because they appear in error-response
   mappings (503 for health, 501/502/503/504 for proxy endpoints).
   The .NET tier never *constructs* a `HealthStatus` (it just receives
   them from the ml tier through the Kiota client), so the
   `ApiException` base is irrelevant. The proxy endpoints construct a
   small hand-written `WireProblemDetails` record for the *response* —
   that keeps `System.Text.Json` happy without owning the wire schema.

4. **`SnakeCaseEnumConverter` (briefly added, then removed).** An
   earlier draft of the .NET client used hand-rolled POCOs with a
   custom enum converter for the snake_case wire spelling. Once the
   Kiota-generated tree replaced the hand-rolled snapshot, the
   converter became dead code — Kiota's own serializers consume
   `[EnumMember(Value=...)]` directly. The companion test was rewritten
   as `GeneratedEnumWireSpellingTests` which asserts the attribute
   values on the *generated* enums, locking the wire spelling at the
   contract layer.

5. **Slice-1 `/api/alerts/stream` is contract-declared but excluded
   from the ml-tier ContractValidator.** The endpoint lives on the .NET
   tier (it's an SSE channel the web tier serves). The YAML documents
   it for cross-tier audit purposes; the validator filters it out of
   the ml-tier emission comparison so the slice-1 wire description
   doesn't need to be split across two YAMLs.

6. **Kiota's `kiota-lock.json` and `.kiota.log` ARE committed.** The
   lock file carries the SHA-512 of the description plus the CLI
   flags — committing it makes accidental drift visible in `git diff`
   and gives the regen script a way to prove "this output came from
   this contract version." The log file is one line of harmless
   warnings about multiple `servers` entries in the YAML; committing
   it keeps `git status` clean after a regen.

7. **Kiota CLI pinned to `1.31.1` (not the latest `2.x` preview).**
   1.31.1 is the most recent stable release that ships dlls for net8,
   net9, AND net10. Bumping to 2.x would require the developer to
   either pin Kiota to a 1.x or upgrade in lock-step with the
   abstractions (Kiota 2 reorganised the runtime API).

## Inherited slice-1 gotchas

* `.NET 10 GA image tags only` — confirmed: `mcr.microsoft.com/dotnet/sdk:10.0`
  and `aspnet:10.0`, no `-preview`. Build succeeds.
* `InvariantGlobalization=false` + `libicu-dev` — unchanged from slice 1.
  `Microsoft.Data.SqlClient` still works in the slice-2 image.
* No `CS8120` regressions — `dotnet build -nologo` reports `0 Warning(s), 0 Error(s)`.
* Uvicorn JSON logging — slice 2's `lifespan` calls `configure_logging()`
  exactly once at startup; no `--log-config` override on the CMD.

## Pattern-threshold provenance (carried over from slice 1)

The `dbo.fn_classify_pattern` constants in `scripts/init-db.sql` are
unchanged in slice 2. Slice 10 (calendar-conditioned profiles, #12)
is the proper home for empirical re-derivation; slice 2's stub for
`POST /api/profiles/analyze` doesn't compute anything.
