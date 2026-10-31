# ADR 0004: Use Testcontainers for integration tests

- **Status:** Accepted
- **Date:** 2026-10-31

## Context

The application depends on PostgreSQL for persistence and RabbitMQ for job delivery. Unit tests can use in-memory substitutes, but they do not verify that the real database schema, SQL operations, broker topology, and consumer wiring work together. Integration tests should also be repeatable locally and suitable for a future CI pipeline gate without depending on manually started shared services.

## Decision

Use Testcontainers for .NET to provision isolated PostgreSQL and RabbitMQ containers for integration tests. Tests own the lifecycle of their containers and clean them up when the test fixture is disposed. Use a local test HTTP server for crawl targets so integration tests do not depend on external websites.

The integration test should exercise the real crawl consumer and MassTransit RabbitMQ transport, with the production endpoint configuration, against the real `CrawlStore` and PostgreSQL schema.

## Rationale

- Testcontainers provides disposable, isolated dependencies instead of relying on a developer's Compose stack or shared database state.
- The same test setup can run locally and in CI environments that provide a Docker-compatible container runtime.
- A local HTTP test server keeps crawl content deterministic and network-independent.

## Consequences

- Running integration tests requires Docker or another compatible container runtime.
- Tests start PostgreSQL and RabbitMQ themselves; developers do not need to start the application Compose stack first.
- The test fixture is responsible for container startup, schema initialization, and reliable disposal.
- Container images and startup time are part of integration-test execution and should be considered when configuring CI.
