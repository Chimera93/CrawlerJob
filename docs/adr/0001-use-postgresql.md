# ADR 0001: Use PostgreSQL for crawl-job persistence

- **Status:** Accepted
- **Date:** 2026-10-31

## Context

The application needs a SQL database to persist crawl jobs, discovered pages, and parent-child link edges. The assignment allows PostgreSQL, SQL Server, or MySQL. The project should be straightforward to run locally and portable for development and review.

## Decision

Use PostgreSQL, run as a Docker Compose service using the `postgres:16-alpine` image. Use Npgsql for .NET database access. pgAdmin may be used as an optional local tool when manual database inspection or queries are useful; it is not required by the application or its normal startup flow.

## Rationale

- PostgreSQL has a lightweight, portable container image that fits the Compose-based local setup.
- It avoids the higher local resource and setup overhead expected for SQL Server in this time-boxed project.
- pgAdmin is available if a GUI is helpful, without adding a database GUI dependency to the core application.

## Consequences

- The API and worker use PostgreSQL connection strings and Npgsql.
- The Compose stack persists database data in the `crawler-data` volume.
- Developers can connect an optional pgAdmin instance to the Compose database using host `db` from within the Compose network, or `localhost:5432` from the host.
- SQL Server-specific behavior and tooling are not part of this implementation.
