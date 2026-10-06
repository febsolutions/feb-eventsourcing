# 0007 — MongoDB works without a replica set

- **Status:** Accepted
- **Date:** 2026-08-14 · amended 2026-09-12

## Context

Appending events touches two collections: the per-aggregate version document and the
events. Atomic writes across both require multi-document transactions, which MongoDB
only offers on replica sets. Many installations run standalone servers.

Two-step writes have windows where the steps diverge. Beyond the obvious one (the
process dies between the steps), a less obvious one matters in practice: **a write can
be committed on the server while the client sees an error** — a timeout or a
replica-set election. Retry logic in the driver only hides the first such error.
Earlier versions healed only one direction of divergence, so a stream could become
permanently unwritable, and a duplicate-key error on insert was treated as an
idempotent retry, which could silently discard new events.

## Decision

The default is a **two-step protocol that does not need a replica set**:

1. Compare-and-set on the version document (`expected` → `expected + n`) — the
   concurrency anchor.
2. Insert the events; a **unique index on (aggregate id, version)** makes duplicates
   impossible. On a duplicate key, existing documents are compared **by event id**: our
   own events mean a retry (insert the rest), a foreign event means a conflict.
3. If the insert fails, the version is rolled back (best effort).
4. **Stored events are the truth.** On every full load, the version document is
   reconciled with the events actually stored, in both directions (ahead → rolled
   back, behind → advanced, missing → recreated).

On replica sets (a single-node replica set suffices), `UseTransactions()` runs both
steps in one multi-document transaction. It is **opt-in**; the two-step protocol stays
the default.

`ConcurrencyException<TId>` carries the version the store actually holds
(`ActualVersion`) for diagnosis.

## Consequences

- Works on standalone servers; no intermediate state can block a stream for good.
- A write the server committed counts as done even if the caller saw an error — the
  same semantics as any database write whose acknowledgement is lost.
- Atomicity across *different* aggregates is not provided by either mode.
- The test suite runs MongoDB as a single-node replica set to cover both modes.
