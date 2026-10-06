# 0008 — The store chain is ordered by layer, not call order

- **Status:** Accepted
- **Date:** 2026-08-14

## Context

`IEventStore<TAggregate, TId>` is a chain of decorators around the core store:
snapshots, the Redis cache, application-specific layers. Their order matters — the
cache must sit *outside* the snapshot layer, or a cache hit would still pay for a
snapshot load. When the order followed the sequence of `Use*()` calls, swapping two
lines in the configuration silently produced a wrong chain.

## Decision

Every registration declares its position via `IEventStoreRegistration.Order`. Well-known
positions are constants on `EventStoreLayer`: `Core = 0`, `Snapshots = 100`,
`Cache = 200`, `CrossCutting = 300`. Layers are sorted ascending (lower = closer to the
core); equal values keep registration order. Custom layers pick a value between the
well-known ones.

## Consequences

- Configuration order no longer matters; the chain is always Cache → Snapshots → Core.
- Custom store registrations must implement `Order`.
