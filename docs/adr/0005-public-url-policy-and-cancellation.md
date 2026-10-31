# ADR 0005: Public URL policy and cooperative cancellation

## Status

Accepted

## Context

The API accepts user-supplied URLs and the worker makes outbound HTTP requests. Without URL and address controls, a submitted URL or redirect could target loopback, private, link-local, or other non-routable services. DNS resolution can also change between validation and connection. Long crawls need a way to stop work without abandoning an in-flight HTTP request.

## Decisions

- Accept only absolute HTTP and HTTPS URLs without embedded credentials.
- Reject literal IPs and DNS results unless every resolved address is globally routable. Apply the same validation before API job creation and before each worker request.
- Resolve and validate again in the worker's socket connection callback, disable outbound proxies, and connect directly to a validated address to mitigate DNS rebinding and proxy-based bypasses.
- Disable automatic redirects. Follow at most five redirects manually and reject any redirect whose host differs from the original job host; validate each destination before requesting it.
- Expose `POST /api/jobs/{jobId}/cancel`. Only Pending and Running jobs can be canceled; an unknown ID returns 404 and a terminal job returns 409.
- Cancellation is cooperative. Let the active HTTP request, body read, parse, and persistence finish, then stop before starting another page. Persist `Canceled` as the terminal state and use `completed_at` for its timestamp.
- The UI offers cancellation for pending/running jobs and renders any pages persisted before cancellation.

## Consequences

- Conservative address filtering may reject a hostname that has a mix of public and non-public DNS answers, even if a particular request could have used a public answer.
- DNS is resolved at validation and connection time; no unchecked address is used for the outbound socket. Network-level egress restrictions remain recommended defense in depth.
- Cancellation can take as long as the current request timeout (10 seconds), and the current page can be included in the partial results.
- The job schema does not distinguish completed and canceled timestamps; clients interpret `completed_at` according to the terminal status.
