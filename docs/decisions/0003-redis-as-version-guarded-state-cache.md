# 0003 — Redis caches aggregate state with version-guarded writes

- **Status:** Accepted
- **Date:** 2026-08-14

## Context

Applications often display lists that load many aggregates at once. Replaying each
stream — even from a snapshot — on every such read is too slow, so a shared cache is
needed. Two shapes were possible: cache the *events* (still requires a replay per
read) or cache the *current state* (one read, no replay).

A shared cache written by several processes has a classic race: a process with an
older state can write after a process with a newer one and leave stale data in the
cache until it expires.

## Decision

Redis caches the **current aggregate state** as the generated snapshot DTO, one hash
per aggregate with stream version, schema version, payload, compression and creation
time.

- **Writes are compare-and-set** in a Lua script: an entry is only replaced by one with
  a *higher* stream version. An older state can never overwrite a newer one.
- **Cache-aside** on reads (a miss loads from the store and fills the cache) and
  **write-through** on saves.
- On a `ConcurrencyException` the entry is **invalidated**, because the writer's state
  was stale.
- Entries carry the **schema version** of the state contract; a mismatch is a miss.
- Entries expire by **TTL with jitter** (`SetTtl`, `SetTtlJitter`), so they do not all
  expire at once.
- Cache failures **never fail a command**: they are logged and the store is used.

## Consequences

- Hot aggregates are served with one Redis round trip; correctness still rests on the
  append version check ([0001](0001-optimistic-concurrency-is-the-correctness-anchor.md)).
- Only aggregates with generated state contracts (`[AutoSnapshot]`) are cacheable.
- A changed state contract makes existing entries unreadable; they are treated as
  misses and expire — no migration.
