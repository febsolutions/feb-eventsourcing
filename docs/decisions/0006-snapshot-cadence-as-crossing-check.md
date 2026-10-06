# 0006 — Snapshot cadence is a crossing check

- **Status:** Accepted
- **Date:** 2026-08-14

## Context

"Snapshot every N events" was originally implemented as `version % N == 0` after a
save. A command can raise several events, so the version can jump over the multiple
(for example from 8 to 12 with N = 10) — and from then on, no snapshot is ever
written again for that aggregate.

Writing the snapshot also adds latency to the command, although it is only an
optimization.

## Decision

- A snapshot is due when the **event count crosses a multiple of N** during the save:
  `floor((after + 1) / N) > floor((before + 1) / N)`, with the event count being
  version + 1.
- Snapshot writes can be taken off the command path with
  `UseBackgroundSnapshotWrites()`: a bounded queue drained by a hosted service. When
  the queue is full, the job is dropped — the next crossing catches up. Inline writes
  remain the default.

## Consequences

- Multi-event commands no longer skip snapshots.
- With background writes, a crash can lose a pending snapshot; that is harmless by
  [0001](0001-optimistic-concurrency-is-the-correctness-anchor.md).
