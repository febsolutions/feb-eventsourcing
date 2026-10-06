# 0015 — Outbox envelopes are written atomically with the events

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

An outbox only guarantees delivery of every stored event if a stored event can never
exist without its envelope. Originally the event store wrote the events, then ran the
projections, and only then enqueued the envelopes — each as a separate write. Stored
events could therefore miss their envelopes, silently and without any trace in the
outbox, whenever

- a projection threw (the enqueue was never reached) — the most frequent case, since
  any read-model timeout is enough;
- the process stopped between the writes (deployment, crash);
- the outbox write itself failed, or failed after some of several envelopes.

## Decision

- Persistences that can do so write the envelopes **in the same atomic operation** as
  the events, through the optional interface `IAtomicOutboxAppend`:
  - PostgreSQL and SQL Server: one transaction for version check, events and envelopes.
  - MongoDB with `UseTransactions()`: one multi-document transaction.

  If the outbox write fails, the append is rolled back as a whole. The atomic path is
  only used when the registered outbox lives in the same store; a different outbox is
  never bypassed.
- Otherwise — MongoDB without a replica set, persistences without the interface, custom
  outboxes — the envelopes are written **immediately after the append, in one batch,
  and before the projections**.
- In every store the envelopes are written before the projections, so a failing read
  model never suppresses delivery of stored events.
- **MongoDB without a replica set keeps a residual gap** between the event insert and
  the outbox insert. Two collections cannot be written atomically without
  transactions; this is accepted rather than adding a verification protocol. Deployments
  that need every event delivered run a single-node replica set with `UseTransactions()`.

## Consequences

- On PostgreSQL, SQL Server and MongoDB with transactions, every stored event reaches
  the outbox; delivery is then at-least-once per subscriber
  ([0009](0009-outbox-named-subscribers.md)).
- `SaveAsync` can fail with a projection error although the events are stored *and*
  will be delivered — consistent with "stored events are the truth"
  ([0007](0007-mongodb-without-replica-set.md)).
- Public interfaces did not change: `IAtomicOutboxAppend` is optional, and
  `IOutboxPersistence.EnqueueManyAsync` has a default implementation.
