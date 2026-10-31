# ADR 0006: Development Swagger UI

## Status

Accepted

## Context

The assessment's time-box emphasized delivering and verifying the end-to-end crawl flow. Implementation and verification therefore prioritized backend behavior, integration tests, and CLI-driven checks before adding interactive API documentation. Once the Compose stack and UI flow worked end to end, Swagger UI became a useful follow-up for reviewers and developers exploring the API contract.

In a typical project, API documentation would be established earlier and maintained alongside the endpoints. Here, deferring it kept the initial effort focused on the core assessment behavior; Swagger is now added as a small documentation and exploration aid.

## Decision

- Generate an OpenAPI document for the Crawl API and expose Swagger UI at `/swagger` only when the API host runs in the Development environment.
- Set the local Docker Compose API environment to Development so the documentation is available during local assessment and manual testing.
- Add action summaries, response status descriptions, and response schemas for the job endpoints.
- Leave Swagger disabled by default in production environments.

## Consequences

- Reviewers can inspect and try the API from a browser without relying solely on CLI commands.
- OpenAPI documentation is derived from the controller actions and XML comments, keeping the interactive reference near the API implementation.
- Production deployments must explicitly choose whether to enable and protect API documentation; it is not exposed by default.
