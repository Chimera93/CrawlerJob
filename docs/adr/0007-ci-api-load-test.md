# ADR 0007: Bounded API load test in CI

## Status

Accepted

## Context

The existing CI verifies correctness with unit and container-backed integration tests, but it does not exercise the API under concurrent requests. A small, repeatable load check can catch obvious performance regressions without turning the assessment pipeline into a capacity benchmark.

The test needs to be bounded, avoid crawling third-party sites, and fit within a two-minute load-test budget. k6 models concurrent virtual users (VUs), rather than dedicated operating-system threads.

## Decision

- Add a k6 scenario that sends concurrent `GET /api/jobs?page=1&pageSize=20` requests to the API backed by the Compose PostgreSQL and RabbitMQ services.
- Use 10 VUs: ramp up over 5 seconds, sustain 10 VUs for 50 seconds, then ramp down over 5 seconds (about 60 seconds total). Pace each VU at one request per second to keep the workload bounded at roughly 10 requests per second.
- Require a p95 response time below 1 second, fewer than 1% failed HTTP requests, and at least 99% successful response checks.
- Run the CI stages in order: unit tests, container-backed integration tests, then API load test. Keep all stages in the existing `Build and test` job so the current required status check covers the load test as well.
- Keep the load scenario read-only and local to the CI Compose stack. Do not submit crawl jobs or depend on external websites.

## Consequences

- The selected 10-VU profile is a modest regression/smoke test, not a production capacity claim or a substitute for a dedicated performance test.
- The k6 workload itself is designed to finish in about one minute, within the two-minute budget. Total CI wall-clock time also includes .NET restore/tests, Docker image builds and startup, and can exceed two minutes.
- The response-time threshold is an initial guardrail for a shared GitHub-hosted runner; revise it only with measured, repeatable evidence.
