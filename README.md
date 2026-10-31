# Website Crawl Jobs

A small .NET 8 / React system for submitting bounded website crawls. The API persists a job and publishes a RabbitMQ message; a separate worker fetches and parses HTML pages, writes page/edge results to PostgreSQL, and the UI polls for progress.

## Run locally

Requirements: Docker Desktop with Compose. From the repository root:

```powershell
docker compose up --build
```

Open the UI at <http://localhost:5173>. API health is at <http://localhost:8080/health>, worker health at <http://localhost:8081/health>, and RabbitMQ management UI at <http://localhost:15672> (default guest/guest).

Stop with `docker compose down`. PostgreSQL data is kept in the `crawler-data` volume; `docker compose down -v` removes that data.

## API

- `POST /api/jobs` — body `{"url":"https://example.com","maxDepth":2}`; returns `202 {"jobId":"..."}`
- `GET /api/jobs/{jobId}` — status, timestamps, page count, and failure reason
- `GET /api/jobs/{jobId}/tree` — discovered-page tree and per-page domain link ratio
- `GET /api/jobs?page=1&pageSize=20` — recent-first, paginated history

`maxDepth` defaults to 2 and is restricted to 0–10. The worker caps a job at 200 distinct normalized URLs.

## Architecture and decisions

- `CrawlerJob` is the HTTP API, `Crawler.Worker` is a separate process, and `Crawler.Infrastructure` holds shared contracts and persistence helpers.
- PostgreSQL is the source of truth. `crawl_jobs` is indexed by creation time; pages are unique on `(job_id, url)`, and edges are unique on `(job_id, parent_url, child_url)`. SQL reads for status/history/tree are separate from crawl execution.
- PostgreSQL was chosen for a portable, lightweight Docker setup; pgAdmin is optional for manual database inspection. See [ADR 0001](docs/adr/0001-use-postgresql.md).
- MassTransit provides typed `CrawlMessage { "jobId": "<guid>" }` publishing and RabbitMQ consumer setup. The worker consumes from `crawl-jobs`. MassTransit uses durable RabbitMQ topology and its default error transport; after retries are exhausted, the original message is moved to `crawl-jobs_error` and a `Fault<CrawlMessage>` is published. Inspect/replay or purge failed messages through RabbitMQ management.
- Integration tests use Testcontainers to start isolated PostgreSQL and RabbitMQ containers; see [ADR 0004](docs/adr/0004-containerized-integration-tests.md).
- MassTransit applies two in-memory retries after the original attempt (1s and 2s intervals). Exceptions from request timeouts, server errors, database access, or parsing are retried; HTTP 4xx responses and non-HTML pages are skipped. On the final attempt the worker marks the job failed and lets the exception propagate so MassTransit routes the failed message to its error queue. The in-memory retry policy is intentionally short and does not provide delayed redelivery across process restarts.
- Page/edge uniqueness plus `ON CONFLICT DO NOTHING` makes persisted crawl results safe under redelivery. A redelivered partial crawl can resume traversal from already-persisted outgoing edges.
- The starting domain is the initial URL's host (case-insensitive). Relative and fragment links are normalized; only HTTP(S) links contribute to the ratio. External HTTP(S) links are recorded as edges and count in the denominator but are not crawled. A page with no eligible links has ratio 0.
- The UI uses polling rather than SSE/SignalR. It shows job state, timestamps, count-based progress, failure text, result tree, and history.

## Testing

Run all .NET tests, including the container-backed crawl pipeline integration test, with Docker running:

```powershell
dotnet test .\Crawler.Tests\Crawler.Tests.csproj
```

Testcontainers starts and removes its own PostgreSQL and RabbitMQ containers. The integration test posts through the real API, verifies the worker's retry and crawl behavior against deterministic local pages, and checks persisted results. No Docker Compose services or external websites are needed for tests. Run only the integration test with:

```powershell
dotnet test .\Crawler.Tests\Crawler.Tests.csproj --filter Category=Integration
```

## Delivery approach

Work is prioritized as ranked vertical slices: (1) submit a URL and acknowledge a persisted/published job; (2) crawl bounded HTML and calculate link metrics; (3) persist lifecycle/progress and idempotent page/edge results; (4) inspect the completed result tree; (5) browse job history; and (6) run and observe the full local stack. Each slice aims to provide an end-to-end user capability instead of completing all backend or frontend work separately. The detailed acceptance checks and current status are in [IMPLEMENTATION-NOTES.md](IMPLEMENTATION-NOTES.md).

Use a test-first loop where practical: derive a focused test from the requirement, observe it fail, implement the smallest behavior to pass, then refactor with tests green. Prefer unit tests for deterministic URL and metric rules, fixture tests for HTML parsing, and integration tests for API/database/broker boundaries. Existing tests were written after the initial implementation rather than strictly test-first; the remaining test backlog is documented in the implementation notes.

## What was prioritized, cut, and next

- **Implemented first:** durable job lifecycle and clear API/worker boundaries, then bounded crawl persistence and messaging. Those paths establish the assignment's core asynchronous flow.
- **Cut / simplified:** no cancellation endpoint, authentication, rate limiting, configurable page limit, content rendering, or SSE. Direct publish follows the job insert rather than using a transactional outbox.
- **Next with more time:** add a transactional outbox and stronger poison-message replay/observability, delayed redelivery across process restarts, load/performance tests, stricter public-URL/SSRF controls and crawl politeness limits, a UI pagination control, and CI build/test automation. The current worker's 200-page ceiling is a constant and should become configuration.

See [IMPLEMENTATION-NOTES.md](IMPLEMENTATION-NOTES.md) for the ongoing requirement breakdown and decision log.
