# CrawlerJob Project Timeline

This timeline reconstructs the project from the available Copilot session history, repository commits, and pull-request merge commits. The work was concentrated on **October 31, 2026**; entries are ordered by implementation sequence rather than exact time. Some conversation history was summarized, so this is a milestone record, not a transcript.

## Implementation timeline

### 1. Assignment breakdown and initial application

- Captured the original full-stack crawler assignment in [`docs/home-assignment-prompt.md`](docs/home-assignment-prompt.md) and broke it into ranked vertical slices.
- Chose to build a working end-to-end capability slice by slice and use tests derived from user requirements. In practice, some initial implementation preceded its tests; this was called out in the implementation notes rather than described as strict test-first development.
- Renamed the starter API project from `WebApplication1` to `CrawlerJob` and added a .NET/Visual Studio `.gitignore`.
- The first slice established job submission: validate and normalize the URL, persist a Pending job, publish a crawl message, and return a job ID.

### 2. Architecture decisions and bounded crawling

- Selected **MassTransit with RabbitMQ** over native RabbitMQ APIs for typed messaging and simpler .NET integration under the time constraint. The worker uses two short in-memory retries; exhausted messages go to MassTransit’s error queue.
- Selected **PostgreSQL** for a portable, lightweight Docker Compose setup. Chose **selective EF Core** for conventional CRUD if useful, while keeping specialized or PostgreSQL-specific operations as SQL rather than introducing an ORM everywhere. See [ADR 0001](docs/adr/0001-use-postgresql.md) and [ADR 0002](docs/adr/0002-use-ef-core-selectively.md).
- Added the bounded crawl behavior: URL normalization, fragment and duplicate handling, depth limits, same-host traversal, link extraction, Domain Link Ratio calculation, and a fixed 200-URL safety cap. The fixed cap was considered sufficient for the assignment; configurability was deferred. See [ADR 0003](docs/adr/0003-bound-crawl-url-attempts.md).
- Added unit/consumer tests for submission and crawling, then installed the .NET 8 SDK/runtime so tests could run without runtime roll-forward.

### 3. Real infrastructure and recovery behavior

- Added Testcontainers integration coverage using PostgreSQL and RabbitMQ, plus a local deterministic HTTP fixture. Tests progressed from directly exercising storage/messaging to submitting through the real API and verifying persisted results.
- Expanded coverage to include transient retries, partial-crawl recovery, duplicate and concurrent message delivery, idempotent page/edge persistence, job details, result trees, history pagination, and missing/invalid requests.
- This sequence closed the gap between fake-store unit tests and the real database/broker path. The integration setup and rationale are recorded in [ADR 0004](docs/adr/0004-containerized-integration-tests.md).
- Explicitly verified poison-message handling: after the original attempt and two retries, a persistently failing crawl is marked Failed and its message is routed to `crawl-jobs_error`.

### 4. Security and cancellation

- Although not required by the assignment, chose to implement SSRF protection and job cancellation before treating the crawler as usable with untrusted URLs.
- The URL policy allows globally routable addresses, rejects unsafe DNS answers and embedded credentials, pins outbound connections to validated IPs, and prevents redirects from changing host.
- Cancellation is cooperative: finish the in-flight HTTP request, persist its result, then stop before requesting another page. The API and UI expose cancellation and retain partial results.
- Documented the policy in [ADR 0005](docs/adr/0005-public-url-policy-and-cancellation.md). The .NET suite and frontend production build passed after this work.
- Authentication and rate limiting were deliberately left to production hosting infrastructure (such as an API gateway and ingress/load balancer); local Compose is development-only.

### 5. Compose verification and developer documentation

- Ran the full API, worker, PostgreSQL, RabbitMQ, and React stack and completed a crawl through the UI.
- **Setback:** Docker initially failed while building because WSL/Docker storage was unhealthy; the WSL update also ran out of C: drive space (about 914 MB available where roughly 1.5 GB was needed). After freeing space and updating WSL, BuildKit still produced `exec /bin/sh: exec format error` for .NET image build steps. Building with Docker’s legacy builder succeeded; a transient npm registry 404 cleared on retry.
- Verified API and worker health, UI availability, and a completed crawl with a result tree. Added reproducible local steps and a supplied screenshot to the README; the user later confirmed the documented manual flow worked.

### 6. Swagger, CI, and load testing

- Added **Development-only Swagger UI** after end-to-end behavior had been verified. This was intentionally a follow-up rather than an early priority: under the time box, CLI and integration testing were more useful first. See [ADR 0006](docs/adr/0006-development-swagger-ui.md).
- Added GitHub Actions CI and documented that **Build and test** must be configured as a required check for `main`.
- Created a `load-testing` branch and added a k6 history-endpoint scenario with 10 virtual users, paced at about one request per second for about 60 seconds, targeting completion in under two minutes. The test was moved after integration testing in the pipeline.
- The local k6 run completed with 555 requests, zero failures, and 7.63 ms p95 latency. This is a bounded regression check, **not** a throughput or capacity guarantee.
- **Setback:** a push to `main` was rejected by GitHub rule GH013 because the required “Build and test” status check was expected. This exposed the dependency between branch protection configuration and the workflow’s published check.
- Deferred representative capacity testing because no target workload, data volume, resource profile, or service-level objective had been defined. See [ADR 0007](docs/adr/0007-ci-api-load-test.md) and [ADR 0008](docs/adr/0008-defer-capacity-testing.md).

### 7. SQL-focused code review and follow-up

- Created the `code-review` branch from `main` for a review emphasizing SQL query shape and per-page database work.
- Recorded findings in [`docs/code-review-2026-10-31.md`](docs/code-review-2026-10-31.md), categorized as Major, Minor, or Nit.
- Fixed the first Major: replaced one awaited SQL insert per discovered edge with one set-based PostgreSQL insert per page, using aligned arrays and `unnest` while preserving `ON CONFLICT DO NOTHING`.
- Added an integration test that saves 300 outgoing edges plus a parent edge twice and verifies one page and 301 unique edges. The focused test passed. The fix was committed as `b7be114` (`Batch crawl edge inserts`).
- The other two Major findings remain **suggestions, not implemented fixes**: enforce a decompressed response-size limit while streaming, and avoid potentially explosive tree expansion by returning a graph-shaped result (or strictly bounding legacy tree expansion). Their proposed approaches are documented in the code-review document.

## Pull-request milestones

The merge commits in the repository show these PRs merged to `main`:

| PR | Milestone |
|---|---|
| [#1](https://github.com/Chimera93/CrawlerJob/pull/1) | Initial crawler application and project foundation |
| [#2](https://github.com/Chimera93/CrawlerJob/pull/2) | Bounded crawl tests and container-backed integration foundation |
| [#3](https://github.com/Chimera93/CrawlerJob/pull/3) | API/database integration, idempotency and recovery coverage, and assignment documentation |
| [#4](https://github.com/Chimera93/CrawlerJob/pull/4) | Retry exhaustion and poison-message/error-queue verification |
| [#5](https://github.com/Chimera93/CrawlerJob/pull/5) | SSRF protections, cooperative cancellation, and Compose/UI verification documentation |
| [#6](https://github.com/Chimera93/CrawlerJob/pull/6) | Development Swagger UI |
| [#7](https://github.com/Chimera93/CrawlerJob/pull/7) | CI build-and-test workflow |
| [#8](https://github.com/Chimera93/CrawlerJob/pull/8) | Required CI status-check documentation |
| [#9](https://github.com/Chimera93/CrawlerJob/pull/9) | Bounded k6 API regression test in CI |

## Tradeoffs and deferred work

- **Prioritized:** complete job lifecycle, async API-to-worker flow, crawl correctness, SQL persistence, retries/idempotency, integration verification, and a working local UI.
- **Simplified:** fixed 200-page limit; polling rather than SSE/SignalR; development-only Swagger; short in-memory retries.
- **Deferred:** transactional outbox, infrastructure-owned authentication/rate limiting, configurable page cap, crawl politeness controls, richer poison-message replay/operations, and a representative capacity test.
- **Current review follow-up:** the edge-insert performance issue is fixed; unbounded response-body memory use and tree expansion remain open Major review findings.

## Commit references

Key repository milestones, in implementation order:

`3e891bb` initial crawler application → `31cc36f` submission tests → `0560c4d` bounded crawl tests → `5e4bf3e` containerized integration tests → `00b4f42` real API submission integration → `5a4cce9` redelivery and partial recovery → `616ee31` API read-path tests → `2daa371` concurrent idempotency → `367644c` retry/DLQ verification → `7cd280d` SSRF and cancellation → `52a82ef` Compose smoke-test steps → `12522ab` UI screenshot → `c47ac0a` Swagger → `4dc6508` CI → `ab0c397` required-check documentation → `9307384` k6 CI test → `88f3801` pipeline stage order → `a800137` code review and capacity-test deferral → `b7be114` batched edge inserts.
