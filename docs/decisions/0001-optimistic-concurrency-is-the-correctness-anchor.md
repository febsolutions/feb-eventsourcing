# 0001 — Optimistic concurrency is the only correctness anchor

- **Status:** Accepted
- **Date:** 2026-08-14

## Context

Event-sourced aggregates are loaded, changed and saved concurrently by different
requests and processes. Besides the event store, the framework has components that
hold derived copies of aggregate state: persistent snapshots and the Redis state
cache. Each of them can be stale — a snapshot is older than the stream by design, a
cache entry may have been written before another process saved.

If correctness depended on these copies being current, every cache invalidation bug,
crash or network partition would become a data-corruption bug.

## Decision

The **version check on append** is the single mechanism that guarantees correctness:
a save states the version it was based on, and the persistence rejects it with
`ConcurrencyException<TId>` if the stream has moved on. Nothing else is trusted for
correctness.

Snapshots and the cache are **optimizations only**. They may be stale, missing or
discarded at any time; a stale copy can at worst make a save fail with a concurrency
conflict, never overwrite newer events.

## Consequences

- Every feature must stay correct with snapshots and cache switched off; tests run
  the providers without them.
- Callers must handle `ConcurrencyException<TId>` — typically by reloading and retrying
  once, or by reporting a conflict (HTTP 409).
- Cache and snapshot code can favour simplicity and availability (fail open, degrade
  to the store) instead of strict consistency.
