# Crawl system: requirements and implementation notes

This is the working breakdown for the home assignment. Update the status and decision log as implementation proceeds.

## Requirements, split by component

| Area | Required behavior | Status |
|---|---|---|
| Crawl API | Validate and create jobs; return a job ID; expose job summary, tree, paginated history, and health | Implemented; needs verification |
| Worker | Consume jobs asynchronously; crawl HTML pages to a bounded depth/page count; calculate each page's domain link ratio | Implemented; needs verification |
| Messaging | RabbitMQ, durable delivery, at-least-once/idempotent processing, retry transient failures, and dead-letter poison messages | Implemented; needs verification |
| SQL storage | Persist job lifecycle, pages, and parent-child edges; avoid duplicate rows; support history and tree reads | Implemented; needs verification |
| React UI | Start crawl, poll job status/progress, render results as a tree, and browse history; include loading/error states | Implemented; needs verification |
| Tests | URL normalization and domain-ratio unit tests, plus a local HTML fixture parser test | Implemented; 3 tests pass |
| Delivery | Docker Compose for API, worker, database, and broker; README with run steps, choices, limitations, and priorities | Implemented; container build not verified because Docker daemon is unavailable |
| Observability | Structured job-aware logs and health endpoints | In progress |

## Component boundaries

- **API (`CrawlerJob`)** owns HTTP validation and read/create endpoints. It persists a job before publishing its message.
- **Infrastructure (`Crawler.Infrastructure`)** shares message/domain types, PostgreSQL access, URL normalization, and ratio calculation. MassTransit handles publishing and consumption in the API and worker hosts.
- **Worker (`Crawler.Worker`)** owns message consumption and crawl execution; it stores discovered page and edge records through the shared store.
- **React client** will call the API only; it will poll while jobs are pending/running.
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
| .NET 8 target | Follows the assignment even though the starter project targeted .NET 10. |
| No transactional outbox yet | The API persists the job before publishing with MassTransit. A crash between those operations can leave a pending job without a message; an outbox is a follow-up. |

## Delivery approach: ranked vertical slices

Deliver the system as end-to-end user capabilities rather than finishing every layer in isolation. Although the assignment is delivered at once, use this ranking to prioritize implementation and verification. Each slice should have a visible user outcome and a testable acceptance boundary.

| Rank | Vertical slice | User-visible outcome and acceptance checks | Status |
|---|---|---|---|
| 1 | Submit and acknowledge a crawl | User submits a valid HTTP(S) URL; API returns a job ID; the job is stored as Pending and a typed message is published. Invalid URL/depth is rejected. | Implemented; needs API/message integration coverage |
| 2 | Crawl a bounded site and calculate page metrics | Worker fetches HTML, resolves relative URLs, ignores fragments and non-HTTP schemes, avoids duplicate URLs, respects depth/page caps, and computes the starting-host link ratio. | Implemented; core URL/parser/ratio tests pass; crawl-loop cases need more coverage |
| 3 | Track lifecycle and persist idempotent results | Job moves through Running to Completed or Failed; progress updates; pages and parent-child edges persist once despite redelivery. | Implemented; needs persistence/redelivery integration coverage |
| 4 | Inspect a completed crawl | User opens job details and sees timestamps, progress, failure reason where applicable, and a nested result tree with ratios. | Implemented; needs API/UI end-to-end verification |
| 5 | Find prior jobs | User opens history, sees recent jobs and statuses, and can navigate back to details. | Implemented; needs UI/API end-to-end verification |
| 6 | Run and operate the system locally | Compose starts API, worker, PostgreSQL, RabbitMQ, and React; health endpoints and structured job-aware logs support diagnosis; failed messages reach MassTransit's error queue. | Implemented; Compose syntax validated; image/runtime verification blocked by unavailable Docker daemon |

### Test-driven implementation decision

For each slice, derive focused tests from the user requirement before changing implementation: write a failing test for a behavior or edge case, implement the smallest change that passes, then refactor while keeping the tests green. Prefer fast unit tests for deterministic domain rules; use fixture-backed tests for HTML parsing and integration tests only where behavior crosses process, broker, or database boundaries.

Tests should assert user-facing behavior and meaningful edge cases, not implementation details. The current three tests cover URL normalization, HTTP(S)-only domain-ratio calculation, and parsing a local HTML fixture. They were added after the initial implementation, so they are not claimed as strict test-first development; apply the red/green/refactor loop to remaining coverage and future changes.

### Test backlog mapped to slices

1. **Submit job:** URL scheme validation and max-depth boundaries; API persists and publishes a job.
2. **Crawl rules:** relative/fragment normalization, duplicate handling, excluded schemes, ratio for zero links and mixed internal/external links, depth and page caps, and non-HTML handling.
3. **Lifecycle/idempotency:** duplicate delivery creates no duplicate page/edge rows; transient failures retry; exhausted retries fail the job and route the message to the error queue.
4. **Results:** tree parent-child structure, external outgoing links, and empty results for a job with no stored pages.
5. **History:** newest-first ordering and pagination boundaries.
6. **Operations:** health checks and Compose-backed smoke test when Docker is available.

## Progress / open work

- Implemented API routes, PostgreSQL schema/access, MassTransit RabbitMQ publishing/consumption, worker crawl loop, React screens, and Compose.
- Still to do: expand tests using the test-first approach, especially API submission, crawl depth/cap, persistence idempotency, lifecycle, history, and tree behavior. Container image build could not be exercised because the local Docker daemon is unavailable.
- No cancellation endpoint or runtime-configurable page limit is planned for the first pass; these are optional or lower priority than required behavior.

## Final handoff summary

Fill this in after verification: what shipped, what was prioritized, what was cut, and what should be done next.
