# Legacy design archive

These documents describe the **original ClimaSense design** — a three-tier,
containerised platform: an ASP.NET Core web app + a Python/FastAPI ML
microservice + a bundled SQL Server 2022, with a replay clock, SSE alert
streaming, ASHRAE-55 comfort scoring, a lag-LR forecaster, and a
three-detector anomaly pipeline. It was a **pre-implementation baseline**
(design grilling sessions, 2026-05-08).

**None of that shipped.** On 2026-06-15 ClimaSense was reimplemented as a
single, read-only **.NET 10** dashboard — the **UPS-room environment
monitor** — that reads an existing `ups3` SQL Server database and deploys to
IIS. There is no Docker, no Python tier, no bundled database, and no replay
clock in the current codebase.

For the application that actually exists, see:

- the repository [`README.md`](../../README.md), and
- the design spec
  [`docs/superpowers/specs/2026-06-15-climasense-ups3-monitor-design.md`](../superpowers/specs/2026-06-15-climasense-ups3-monitor-design.md),
  whose header records exactly what this archive supersedes.

Retained here for history only:

| Path | What it was |
| --- | --- |
| `adr/0001`–`0018` | Architecture Decision Records for the original platform |
| `CONTEXT.md` | Domain glossary for the replay-clock / ML platform vocabulary |
| `slice-notes/SLICE-1..13-NOTES.md` | Per-slice build notes for the original 14-day platform plan |

Nothing in this folder reflects the current code.
