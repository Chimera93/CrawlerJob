# Code review: 2026-10-31

## Scope

Static review of the application source on the `code-review` branch, with extra attention to SQL query shape and per-item database work. The first major finding was fixed in follow-up; other findings remain observations, not fixes.

## Resolved during follow-up

1. **Batched edge persistence** — `Crawler.Infrastructure/CrawlStore.cs`. Replaced the per-edge awaited insert loop with one `INSERT ... SELECT FROM unnest(@parents::text[], @children::text[]) ... ON CONFLICT DO NOTHING` per page. The parent edge and all outgoing edges are submitted in aligned arrays within the existing page transaction. This preserves the unique-key idempotency behavior while reducing database commands from one per edge to one per page. Added `Save_page_batches_high_fan_out_edges_and_remains_idempotent`, an integration test that saves 300 outgoing edges plus the parent edge twice and verifies exactly 301 edges and one page. The integration test passed.

## Major

1. **Response bodies are unbounded in memory** — `Crawler.Worker/CrawlConsumer.cs:92-96,149`. `ResponseHeadersRead` avoids buffering in `HttpClient`, but `ReadAsStringAsync` then reads the entire HTML body into memory. The 200-page cap does not constrain bytes per page, so a large public response can exhaust worker memory, especially across concurrent jobs. Enforce a decompressed response-size limit while streaming and define how oversized pages affect the job.
2. **Tree construction can expand a bounded graph into an enormous response** — `Crawler.Infrastructure/CrawlStore.cs:190-222`. The query loads stored pages and edges, then recursively emits a fresh `PageNode` for every simple path, copying the visited-path set at each branch. Crawled pages can have many cross-links, so shared descendants are repeated for each path; the 200-page limit does not bound edge count or path count. Return a graph-shaped representation with references, or impose a documented bound on tree expansion.

## Minor

1. **History performs an exact full-table count per request** — `Crawler.Infrastructure/CrawlStore.cs:66-74`. `SELECT COUNT(*) FROM crawl_jobs` runs on every history call and must examine all rows or index entries as retained job history grows. Consider whether the UI/API needs an exact total on every request, or use a cached/approximate total or a pagination contract that avoids it.
2. **Progress recounts all pages after every save** — `Crawler.Infrastructure/CrawlStore.cs:168-171`. Each page save recalculates `pages_crawled` with a count over that job's stored pages. This repeats work as the crawl grows (bounded today by 200 pages), and is avoidable by updating progress from the insert result or reading the count only when needed.
3. **Large positive page numbers can overflow the SQL offset** — `CrawlerJob/Controllers/JobsController.cs:92-94`; `Crawler.Infrastructure/CrawlStore.cs:74-75`. Validation accepts any positive `int`, but `(page - 1) * pageSize` is calculated as an `int`. Large values can wrap negative and cause a database error/HTTP 500. Calculate in a wider integer type and reject offsets outside the supported range.
4. **Crawl traversal issues multiple sequential store queries per page** — `Crawler.Worker/CrawlConsumer.cs:63-70,114`. Each queued page checks cancellation and stored-page state via separate store calls, each opening a connection and issuing a query; recovery of already-stored pages adds another outgoing-links query. At the current 200-page cap this is bounded, but adds hundreds of database round trips to a full crawl. Consider combining the cancellation/stored checks or loading persisted crawl state once for resume.

## Nit

1. **History order is not fully deterministic** — `Crawler.Infrastructure/CrawlStore.cs:74`. `created_at` is not unique, so records with equal timestamps can move between offset-paginated pages. Add a unique tie-breaker such as `job_id` to the ordering if stable page boundaries are desired.
