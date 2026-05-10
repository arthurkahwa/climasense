# 8. Explicit single-zone scope

Date: 2026-05-08
Status: Accepted

## Context

The schema has no `LocationId`, `SensorId`, or `ZoneId`. The dataset is one CSV's worth of data, and the README never declared scope explicitly — leaving an implicit single-sensor assumption a careful reviewer would surface ("what about my 12 thermostats?").

Two options were considered: declare single-zone explicitly, or add a `LocationId` FK with a single seed row (multi-zone-ready schema).

## Decision

**Declare explicit single-zone scope** in the README. The schema stays free of `LocationId`. Every measurement table assumes one logical environment.

The README states:

> **Scope:** ClimaSense is a single-zone analytical tool driven by one sensor's history. Multi-sensor / multi-zone support is intentionally out of scope; the schema and queries assume one logical environment.

## Consequences

- No `WHERE LocationId = @id` overhead in queries, no `?location=` parameter on API paths, no location picker in the UI.
- Every EF migration, Dapper query, and test fixture stays simpler.
- Future multi-zone support is a textbook migration: `ALTER TABLE ADD COLUMN LocationId INT NOT NULL DEFAULT 1`, FK + index, parameter threaded through queries. One afternoon's work, deferred until needed.
- The README's portfolio pitch becomes more honest — declaring scope rather than implying coverage we don't have.
