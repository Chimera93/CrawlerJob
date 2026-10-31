# ADR 0003: Bound crawls by unique discovered URLs

- **Status:** Accepted
- **Date:** 2026-10-31

## Context

A crawl must remain bounded, and links found in HTML may not be reachable when the worker requests them. Requiring a reachability check before adding a URL would add network work without guaranteeing that a later crawl request will succeed.

## Decision

Count each unique normalized URL discovered for a job toward the 200-URL limit, including the starting URL. Do not require a separate reachability check before queueing an eligible URL: attempt to crawl it when reached in the queue. A URL that responds with an unsuccessful client status or non-HTML content is skipped rather than saved as a page. Transient server failures continue through the existing message retry policy.

Only HTTP(S) links on the starting host are eligible for crawling. Crawl depth continues to limit traversal independently of the unique-URL ceiling.

## Rationale

- A unique-URL ceiling bounds both queue growth and request volume even when discovered pages cannot be fetched.
- Attempting discovered URLs avoids incorrectly treating temporary or later-resolvable URLs as known-unreachable.
- A host boundary and depth limit constrain traversal without preflight requests.

## Consequences

- The 200-URL budget includes the start URL and may include URLs that do not yield a saved page.
- Each normalized URL is queued at most once in a crawl execution; fragments and default ports do not create duplicate crawl targets.
- Unsuccessful client responses and non-HTML responses do not create page records. Server errors remain eligible for MassTransit retries.
