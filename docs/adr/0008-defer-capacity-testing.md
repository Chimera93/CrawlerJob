# ADR 0008: Defer capacity and throughput testing

## Status

Accepted

## Context

The CI workflow includes a bounded k6 regression scenario for the read-only job-history endpoint. It uses 10 virtual users for about one minute and guards response latency and error rate. This provides a repeatable smoke check, but it does not exercise crawl submission/processing throughput or establish a service capacity limit.

The assignment requests SQL performance considerations but defines no request rate, workload mix, data volume, deployment resources, or service-level target. A capacity result without those inputs would be easy to misinterpret and would not reliably represent production behavior.

## Decision

- Keep the existing CI k6 scenario as a short API regression check.
- Defer a throughput/capacity benchmark until a representative workload, data set, resource profile, and target are agreed.
- Do not describe the current CI load check as a capacity guarantee.

## Consequences

- CI stays bounded and does not imply a production throughput commitment.
- A future capacity test should identify its workload and target (for example, sustained requests or completed crawls per second), use representative data and resources, and report latency/error thresholds alongside throughput.
