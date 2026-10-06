# 0010 — Outbox retention and dequeue ordering

- **Status:** Accepted
- **Date:** 2026-09-02 (index and ordering) · amended 2026-09-06 (retention)

## Context

Completed envelopes were originally only marked as dispatched, never removed: the
outbox grew without bound. Separately, the dequeue query — "pending envelopes, oldest
first, not currently leased" — must be served by an index; with a large backlog, an
index that cannot back the sort turns every lease into a blocking sort over all
pending envelopes. Operators also need to know what ordering they can rely on.

## Decision

- **Retention:** completed envelopes are removed after **7 days** by default
  (`SetCompletedRetention`, `DisableCompletedCleanup`). MongoDB uses a TTL index on the
  completion time; the relational providers delete in bounded batches from the outbox
  worker (`SetCleanupInterval`, `SetCleanupBatchSize`). **Dead letters are never
  removed** automatically.
- **Index and query are designed together:** MongoDB indexes `(DispatchedAt, CreatedAt)`
  and checks the lease residually; PostgreSQL and SQL Server use a partial/filtered
  index on `created_at` covering only pending envelopes. Old index definitions are
  migrated automatically at start-up.
- **Ordering:** the dequeue delivers **strictly oldest first** by creation time, across
  batch boundaries; a provider contract test verifies this on every provider.
  Relational batches are sorted in memory because `RETURNING`/`OUTPUT` do not
  guarantee row order.

## Consequences

- Outbox size is bounded by throughput × retention.
- Oldest-first is a property of the *dequeue*, not a global delivery guarantee:
  retries, lease expiry and parallel workers mean a consumer can see a later envelope
  before an earlier, retried one. Consumers that need per-aggregate order use the
  event version.
