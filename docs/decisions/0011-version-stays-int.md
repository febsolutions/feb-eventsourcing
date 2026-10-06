# 0011 — `Version` stays `int`

- **Status:** Accepted
- **Date:** 2026-08-14

## Context

Aggregate and event versions are `int`. Widening them to `long` was considered during a
review of the public API.

## Decision

`Version` stays `int`.

## Consequences

- A single stream is limited to about 2.1 billion events — orders of magnitude beyond
  what an aggregate should ever hold; streams that grow like that indicate a modelling
  problem, not a type problem.
- The change would have touched the public API (`AggregateRoot`, `EventMetadata`,
  `ConcurrencyException<TId>`, persistence contracts) and the stored data of every
  provider, for no practical gain.
