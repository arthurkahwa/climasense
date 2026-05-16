# 12. OpenAPI contract is the single source of truth for the .NET ↔ Python wire format

Date: 2026-05-16
Status: Accepted

## Context

The platform is intentionally polyglot — ASP.NET Core for the web tier,
FastAPI for the ML tier. Wire-format drift between the two is one of
the most expensive failure modes in any polyglot system: a stray
`snake_case` here, an `int?` vs `int` there, and the two ends silently
disagree on a payload until a particular query path is exercised at
runtime, often weeks after the bug landed.

Slice 1 introduced `contracts/openapi.yaml` carrying only the health
probe shape. Slice 2 extends it to the full set of cross-tier
endpoints scoped to slices 7–11 — and, more importantly, wires
codegen into both build pipelines so the contract is no longer
documentation that aspires to truth, it IS the truth.

Three open questions ADR-0015 (PRD #2 "Further Notes") flagged:

1. Which generator on each side?
2. Where does the codegen output live, and is it committed?
3. How is "FastAPI's emission matches the YAML" structurally enforced
   rather than reviewed by hand each PR?

## Decision

### Contract is the single source of truth

`contracts/openapi.yaml` at repo root is the canonical wire-format
description. JSON casing is camelCase on the wire. Every DTO emitted
by either tier is regenerated from this file. Hand-edits to generated
files are not permitted — the regen tool clobbers them.

### Two regenerators

* **.NET tier** — `Microsoft.OpenApi.Kiota` 1.31.1, pinned as a local
  tool in `.config/dotnet-tools.json`. Output: `src/ClimaSense.Web/Generated/MLClient/`.
  Kiota uses `[EnumMember(Value=...)]` plus hand-rolled serializers
  (`IParsable.GetFieldDeserializers` / `ISerializationWriter`) to
  enforce camelCase on the wire — there are no `[JsonPropertyName]`
  attributes in the generated tree.
* **Python tier** — `datamodel-code-generator` (dev dependency).
  Output: `src/ClimaSense.ML/climasense_ml/schemas/generated.py`.
  Pydantic v2 models with `Field(..., alias="camelCase")` per field;
  `--snake-case-field` flag means handler code uses snake_case while
  the wire emits camelCase.

### Both outputs are committed

The generated trees live in version control. Rationale:

* Reviewers without Kiota or `datamodel-code-generator` installed can
  still build and run the project.
* `git diff` after a contract edit shows what changed at the wire
  level, not just at the YAML level.
* CI doesn't need the codegen toolchain — it just builds and tests.

The regen script (`scripts/regen-contracts.sh`) is idempotent: running
it on an unchanged contract produces zero `git diff`. The .NET MSBuild
target `<KiotaRegenerate>` runs the script on `BeforeBuild` so a
contract change can't accidentally skip the regen.

### ContractValidator structurally enforces "FastAPI emission == YAML"

On FastAPI startup, `climasense_ml.contract_validator.validate_contract`
compares `app.openapi()` against `yaml.safe_load(contracts/openapi.yaml)`
along the *wire-significant surface*: paths, methods, operationIds,
response status codes, component schema bodies. Prose
(`description`, `summary`, `example`) is stripped before comparison
so the YAML can stay reviewer-friendly.

On match: `ContractValidator: OK (N paths, M schemas)` at INFO.
On mismatch: `ContractValidator: FAILED — k delta(s)` at ERROR, each
delta named, followed by `ContractMismatchError` propagating out of
the lifespan. Uvicorn exits non-zero. The container's healthcheck
never goes green; compose marks the deployment as failed.

This makes hand-editing `schemas/generated.py` to "fix a typo" impossible
without either (a) re-running the regen or (b) being immediately and
loudly rejected at startup.

### IMLServiceClient is hand-written; Kiota's request builders are the implementation

Per ADR-0011's interface-emergence policy, two adapters justify an
interface. `IMLServiceClient` has two: the production `MLServiceClient`
(which wraps Kiota's `MLApiClient`) and `FakeMLServiceClient` (an
in-memory stub planned for slice 7+ unit tests). The interface ships
in slice 2.

Method shape is *domain*-shaped (`GetForecastAsync(int horizonHours, ...)`),
not Kiota-shaped (`Api.Forecast.GetAsync(cfg => …)`). Callers depend on
the interface; the implementation depends on Kiota. Regenerating the
Kiota tree doesn't ripple beyond `MLServiceClient.cs`.

### Failure semantics live on the HttpClient pipeline, not in the generated client

The wire-level failure mapping (connection-refused → 503,
upstream 5xx → 502, timeout → 504) is implemented by a
`DelegatingHandler` (`MLFailureMappingHandler`) on the `HttpClient`
that backs Kiota's `IRequestAdapter`. The handler is independent of
Kiota — regenerating the client doesn't touch the failure mapping;
swapping Kiota for a different generator wouldn't either.

501 (`Not Implemented`) is intentionally *not* caught by the failure
handler — Kiota's error mapping declares `ProblemDetails` as the
501-response type, so the 501 bubbles up as a typed `ApiException`
that the proxy endpoint surfaces as a 501 to the browser. The wire
contract is preserved end-to-end.

## Consequences

* **Discipline cost:** every contract change is two edits — the YAML
  and a `bash scripts/regen-contracts.sh` run. The MSBuild target
  amortises the .NET side on every `dotnet build`.

* **Build dependency:** the .NET project now depends on six Kiota
  runtime packages (Abstractions + Http + four Serialization-*).
  Together they add ~1 MB to the published image. The wire-correctness
  guarantee is worth the bytes.

* **Generated trees are now load-bearing:** anyone deleting them by
  mistake breaks the build. The .NET MSBuild target re-creates them
  on the next build (Kiota installed) or fails loudly (Kiota missing).

* **Slice-1 hand-written health-endpoint shape is preserved:** the
  slice-1 endpoints continue to return anonymous JSON shaped exactly
  like `HealthStatus`. The contract validator catches drift; the
  reviewer sees a small diff.

* **501 is a first-class wire response, not a failure mode:** the
  slice-2 stubs let the contract surface ship before the implementations
  exist. Slice 7 / 8 / 9 / 10 replace one stub each, not the surface.

* **`kiota-lock.json` is committed.** Carries a SHA-512 of the
  contract and the CLI invocation flags. Accidental drift between
  the YAML and the committed snapshot is grep-friendly: a clean regen
  doesn't change the lock; a sloppy hand-edit to generated code does.

## References

* Issue #4 (slice 2 spec).
* PRD #2 "Implementation Decisions → Cross-cutting" (contracts/openapi.yaml
  as single source of truth, .NET regenerates via Kiota, Python regenerates
  via `datamodel-code-generator`, FastAPI startup assert
  `app.openapi() == load(contracts/openapi.yaml)`, JSON casing camelCase).
* ADR-0011 (interface-emergence policy — justifies `IMLServiceClient`).
* `SLICE-2-NOTES.md` (acceptance-criteria evidence table).
