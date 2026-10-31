# ADR 0002: Use EF Core selectively for data access

- **Status:** Accepted
- **Date:** 2026-10-31

## Context

The application persists crawl jobs, pages, and link edges in PostgreSQL. The current store uses Npgsql with inline SQL. As CRUD needs grow, maintaining common inserts, updates, and straightforward queries as handwritten SQL can become repetitive, while some database operations may still benefit from explicit SQL.

## Decision

Use Entity Framework Core with the Npgsql provider for conventional CRUD queries and other operations where its change tracking, LINQ queries, or model mapping improve clarity and maintainability. EF Core does not need to replace every SQL operation: retain direct SQL for queries or updates where it is clearer, more efficient, or uses PostgreSQL-specific behavior.

## Rationale

- EF Core provides a familiar, strongly typed model for routine data access and can reduce repetitive CRUD SQL.
- Selective use preserves the control and clarity of explicit SQL for specialized operations.
- This is a pragmatic choice, not a requirement to migrate all existing queries immediately.

## Consequences

- When EF Core is introduced, use the Npgsql EF Core provider and keep its model mapping and schema changes aligned with the existing PostgreSQL schema.
- Prefer EF Core for new conventional CRUD work when it simplifies the implementation; choose direct SQL when it is a better fit.
- Existing handwritten SQL can remain until a targeted change provides a clear benefit to migrating it.
