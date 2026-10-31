# Crawl system: requirements and implementation notes

This is the working breakdown for the home assignment. Update the status and decision log as implementation proceeds.

## Requirements, split by component

| Area | Required behavior | Status |
|---|---|---|
| Crawl API | Validate and create jobs; return a job ID; expose job summary, tree, paginated history, cancellation, and health | Implemented; submission, read paths, and cancellation covered by API integration tests |
| Worker | Consume jobs asynchronously; crawl HTML pages to a bounded depth/page count; calculate each page's domain link ratio | Implemented; crawl rules and retry behavior covered by unit and integration tests |
| Messaging | RabbitMQ, durable delivery, at-least-once/idempotent processing, retry transient failures, and dead-letter poison messages | Implemented; Testcontainers verifies real-broker retries, failed-job state, and error-queue delivery |
| SQL storage | Persist job lifecycle, pages, and parent-child edges; avoid duplicate rows; support history and tree reads | Implemented; real PostgreSQL persistence and API reads covered by Testcontainers |
| React UI | Start crawl, poll job status/progress, render results as a tree, and browse history; include loading/error states | Implemented in `frontend/`; browser-level end-to-end verification remains |
| Tests | Unit, API/worker behavior, and container-backed integration tests | Implemented; 37 tests pass, including the container-backed pipeline and Swagger documentation |
| Delivery | Docker Compose for API, worker, database, broker, and frontend; README with run steps, choices, limitations, and priorities; CI correctness and load tests | Implemented; CI runs unit tests, a 10-VU k6 API check, then integration tests on pushes and pull requests targeting `main`; configure **Build and test** as a required check in GitHub branch protection/rulesets |
| Observability | Structured job-aware logs and health endpoints | Implemented; operational behavior still needs a Compose smoke test |

## Component boundaries

- **API (`CrawlerJob`)** owns HTTP validation and read/create endpoints. It persists a job before publishing its message.
- **Infrastructure (`Crawler.Infrastructure`)** shares message/domain types, PostgreSQL access, URL normalization, and ratio calculation. MassTransit handles publishing and consumption in the API and worker hosts.
- **Worker (`Crawler.Worker`)** owns message consumption and crawl execution; it stores discovered page and edge records through the shared store.
- **React client** calls the API only; it polls while jobs are pending/running.
- **URL safety** is enforced at API submission and again for every worker request; the worker pins connections to validated public addresses and rejects cross-host redirects.
- **Cancellation** is a persisted terminal state; worker processing stops after the active response is parsed and saved.
- **PostgreSQL** is the durable source of truth. **RabbitMQ** carries job-start messages; the worker must safely handle redelivery.

## Architecture decisions

| Decision | Reason / trade-off |
|---|---|
| PostgreSQL and RabbitMQ | Both are explicit assignment options and are straightforward to run locally in Compose. |
| PostgreSQL instead of SQL Server | PostgreSQL's lightweight portable Docker image makes local setup simpler and less resource-intensive for this time-sensitive project. pgAdmin is optional for manual inspection, not a required service. See [ADR 0001](docs/adr/0001-use-postgresql.md). |
| MassTransit over the native RabbitMQ client | Chosen for ease of setup, first-class .NET integration (typed messages, dependency injection, consumer configuration, retry/error transport), and the time-sensitive nature of this project. This keeps attention on the crawl flow and idempotent persistence instead of custom broker plumbing. |
| MassTransit retry and error transport | Configure two short in-memory retries; when they are exhausted, MassTransit publishes a fault and moves the message to `crawl-jobs_error`. In-memory retries are simple but are not delayed redelivery that survives a process restart. |
| Separate API, worker, and shared infrastructure projects | Makes the two runtime processes clear while avoiding duplicate persistence and message contracts. |
| At-least-once messages with database uniqueness | A page is unique by `(job_id, url)` and an edge by `(job_id, parent_url, child_url)`; inserts are conflict-safe. This guards result persistence against duplicate deliveries. |
| Bounded crawl | Default depth is 2 and a worker limit of 200 discovered URLs prevents an unbounded crawl. The limit should be surfaced/configurable if time allows. |
| Host-based same-domain traversal | Matches the stated definition of starting domain as the initial URL's host; external links are recorded as outgoing edges but not crawled. |
| Polling for progress | Simpler than SSE/SignalR and sufficient for this assignment. |
| Development Swagger UI | Added after end-to-end verification so reviewers can explore API contracts interactively without delaying the core CLI-driven implementation. Kept off outside Development. See [ADR 0006](docs/adr/0006-development-swagger-ui.md). |
| Bounded API load check in CI | Use k6 with 10 virtual users against the read-only job-history endpoint; run after unit tests and before container integration tests. The roughly 60-second scenario and initial p95/error thresholds are regression guardrails, not a capacity claim. See [ADR 0007](docs/adr/0007-ci-api-load-test.md). |
| Public HTTP URL policy | Reject private/reserved DNS results and credentials; connect to validated IPs to reduce DNS-rebinding risk, and refuse cross-host redirects. This is a conservative defense-in-depth policy, not a replacement for network egress controls. See [ADR 0005](docs/adr/0005-public-url-policy-and-cancellation.md). |
| Cooperative cancellation | The active HTTP request completes and its result is persisted; the worker checks the canceled state before continuing. This avoids tearing down a request mid-flight and returns any partial results. |
| Cancellation timestamp | Reuse `completed_at` for the terminal timestamp because the schema has no separate canceled-at field. |
| .NET 8 target | Follows the assignment even though the starter project targeted .NET 10. |
| No transactional outbox yet | The API persists the job before publishing with MassTransit. A crash between those operations can leave a pending job without a message; an outbox is a follow-up. |

## Delivery approach: ranked vertical slices

Deliver the system as end-to-end user capabilities rather than finishing every layer in isolation. Although the assignment is delivered at once, use this ranking to prioritize implementation and verification. Each slice should have a visible user outcome and a testable acceptance boundary.

| Rank | Vertical slice | User-visible outcome and acceptance checks | Status |
|---|---|---|---|
| 1 | Submit and acknowledge a crawl | User submits a valid HTTP(S) URL; API returns a job ID; the job is persisted and a typed message is published. Invalid URL/depth is rejected. | Implemented; unit and real-broker/database API integration coverage |
| 2 | Crawl a bounded site and calculate page metrics | Worker fetches HTML, resolves relative URLs, ignores fragments and non-HTTP schemes, avoids duplicate URLs, respects depth/page caps, and computes the starting-host link ratio. | Implemented; focused unit and worker tests |
| 3 | Track lifecycle and persist idempotent results | Job moves through Running to Completed, Failed, or Canceled; progress updates; pages and parent-child edges persist once despite redelivery. | Implemented; real PostgreSQL/RabbitMQ tests cover retries, cooperative cancellation, duplicate delivery, and partial-crawl recovery |
| 4 | Inspect a completed crawl | User opens job details and sees timestamps, progress, failure reason where applicable, and a nested result tree with ratios. | Implemented; API read paths tested against containerized PostgreSQL; browser flow remains unverified |
| 5 | Find prior jobs | User opens history, sees recent jobs and statuses, and can navigate back to details. | Implemented; API pagination and history response tested; browser flow remains unverified |
| 6 | Run and operate the system locally | Compose starts API, worker, PostgreSQL, RabbitMQ, and React; health endpoints and structured job-aware logs support diagnosis; failed messages reach MassTransit's error queue. | Error queue verified in Testcontainers; full Compose/UI smoke test remains |

### Test-driven implementation decision

For each slice, derive focused tests from the user requirement before changing implementation: write a failing test for a behavior or edge case, implement the smallest change that passes, then refactor while keeping the tests green. Prefer fast unit tests for deterministic domain rules; use fixture-backed tests for HTML parsing and integration tests only where behavior crosses process, broker, or database boundaries.

Tests should assert user-facing behavior and meaningful edge cases, not implementation details. The current 37 tests cover URL normalization, public-IP URL policy, redirect host restrictions, HTTP(S)-only domain-ratio calculation, HTML parsing, API validation/submission, bounded crawling, cancellation, retry/redelivery, partial-crawl recovery, exhausted-retry error-queue delivery, persisted API read paths, Swagger documentation, and the PostgreSQL/RabbitMQ integration pipeline. They were added as the slices were verified; the original implementation was not developed under a strict test-first process.

### Test backlog mapped to slices

1. **Submit job:** URL scheme validation and max-depth boundaries; API persists and publishes a job — covered.
2. **Crawl rules:** URL normalization, duplicate handling, eligible schemes, link ratios, depth/page caps, retries, and non-HTML behavior — covered by unit/worker/integration tests.
3. **Lifecycle/idempotency:** concurrent and repeated completed-job delivery do not duplicate results; transient failures retry; partial work resumes without duplicate pages/edges; exhausted retries fail the job and route the message to the error queue — covered.
4. **Results:** API detail/tree reads, parent-child structure, metrics, and missing-job behavior — covered; external-edge and empty-result API cases can use more coverage if required.
5. **History:** API history response and invalid pagination — covered; newest-first ordering and multi-page boundary behavior can use more coverage.
6. **Operations:** run the complete Docker Compose stack and smoke-test API, worker, health endpoints, and UI.

## Progress / open work

- Implemented API routes, PostgreSQL schema/access, MassTransit RabbitMQ publishing/consumption, worker crawl loop, React screens, and Compose.
- All 37 .NET tests pass. Testcontainers integration tests have successfully run against real PostgreSQL and RabbitMQ; they also exercise the API through HTTP and crawl against a local deterministic HTTP server.
- Remaining verification: full Compose/browser smoke test. Testcontainers tests require Docker; the UI exists but has not been browser-tested in this assessment.
- CI is implemented in `.github/workflows/ci.yml`; it runs unit tests, a bounded k6 load test, and container-backed integration tests. Repository administrators still need to select **Build and test** as a required status check for `main`.
- Known first-pass cuts: no transactional outbox or configurable page limit. Authentication and rate limiting are intentionally not implemented in the app; in production they should be enforced by the hosting layer (for example, an identity-aware API gateway and ingress/load-balancer rate limits). The local Compose setup is development-only and must not be treated as that boundary. The outbox gap means a crash between job persistence and message publish can leave a pending job without a queued message.

## Final handoff summary

The assessment delivers a .NET 8 API and worker, PostgreSQL persistence, RabbitMQ messaging, a React UI, Docker Compose, and a GitHub Actions workflow with unit, k6 load, and integration-test stages. The bounded load profile exercises the API history read with 10 concurrent virtual users and is not a capacity guarantee. Verification prioritizes the submission-to-crawl flow, bounded crawl rules, retry/redelivery behavior, persistence, API read paths, and Testcontainers isolation. SSRF defenses and cooperative cancellation are implemented and covered by tests. Remaining work is a full Compose/UI smoke test and configuring the CI check as required in GitHub branch protection. Deferred items include an outbox and configurable crawl limits. Authentication and rate limiting are deployment responsibilities for a production hosting boundary, not application features in this assessment.
