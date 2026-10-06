# 0009 — The outbox tracks delivery per named subscriber

- **Status:** Accepted
- **Date:** 2026-08-14

## Context

The transactional outbox hands events to consumers that may be unavailable — message
brokers, external APIs, in-process async handlers. With several consumers, partial
failure is the normal case: one consumer succeeds, another fails. If an envelope were
retried as a whole, every consumer would receive it again on each retry, and one
permanently failing consumer would block or flood the others.

## Decision

- Consumers are **named subscribers** (`IOutboxSubscriber`, registered with
  `UseOutboxSubscriber<T>()`). The name is a **persistence key**: stable, non-empty,
  without `.`, `$` or whitespace.
- Delivery state — attempts, last error, dispatched, dead-lettered — is tracked **per
  subscriber** on each envelope. Retries and dead-lettering happen per subscriber; a
  failing subscriber never causes redelivery to the others.
- An envelope is complete when every known subscriber is terminal (dispatched or
  dead-lettered).
- Delivery is **at-least-once**: a crash between delivery and marking it repeats the
  delivery, so consumers must be idempotent.
- The default subscriber `handlers` delivers to `IASyncEventHandler<T>` in process. A
  message broker is typically *one* subscriber; fan-out happens in the broker.
- The previous single-consumer `IOutboxDispatcher` remains usable as an adapter
  (`UseOutboxDispatcher<T>()`).

## Consequences

- Consumers fail and recover independently.
- A subscriber added later only sees envelopes created after it was registered — there
  is no automatic backfill.
- Renaming a subscriber starts a new delivery history; treat names like column names.
