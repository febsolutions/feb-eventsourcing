# 0002 — Snapshots and the state cache are separate modules

- **Status:** Accepted
- **Date:** 2026-08-14

## Context

Persistent snapshots and an in-memory state cache look alike — both store an
aggregate's state — but they answer different questions:

| | Persistent snapshot | State cache (Redis) |
|---|---|---|
| Purpose | shorten replay of long streams | cut latency of hot reads |
| Lifetime | durable, stored next to the stream | volatile, expires by TTL |
| Schema evolution | real concern, old data must be recognised | entries are simply discarded |
| On failure | full replay (slow, correct) | cache miss (only slower) |

An earlier design shared envelopes, serializers and registry semantics between the
two. The coupling caused double serialization, unclear version semantics and defects
that went unnoticed because each half masked the other.

## Decision

Snapshots (`FEB.EventSourcing.Snapshots`) and the Redis cache
(`FEB.EventSourcing.Redis`) are **independent modules** that do not reference each
other. The only shared piece is the generated state contract — snapshot DTOs and
their metadata — in the dependency-free package `FEB.EventSourcing.StateContracts`.

## Consequences

- Each module can be used alone or both together; their order in the store chain is
  fixed by layer (see [0008](0008-store-chain-ordered-by-layer.md)).
- Each module owns its storage format and versioning rules.
- Changing the state contract affects both — it is the one place where they meet.
