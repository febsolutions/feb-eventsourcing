# 0004 — The snapshot generator is fail-closed

- **Status:** Accepted
- **Date:** 2026-08-14

## Context

Snapshot and cache code is generated at compile time from `[AutoSnapshot]` aggregates.
The dangerous failure mode of any such mapping is *silence*: a member that is not
mapped is simply missing after a restore, and the aggregate continues with a default
value. This surfaces much later, as wrong behaviour in production, if at all.

Typical sources: state in base classes, state kept in fields behind get-only
properties, computed properties, and nested types whose shape changes while old
snapshots still exist.

## Decision

Every member of a snapshotted type is either **mapped**, explicitly excluded with
**`[IgnoreSnapshot]`**, or a **compile error**:

- The generator walks the whole inheritance chain declared in source, not just the
  type itself.
- Instance fields are an error (ASG005); properties without a reachable setter —
  including computed ones — are an error (ASG002); unsupported types are an error
  (ASG004).
- The schema version is a hash over the full shape, **recursively** including nested
  types, so a change anywhere invalidates old snapshots instead of silently
  deserializing them with defaults.
- The `FEB.EventSourcing.TestKit` round-trip contract
  (`SnapshotContract.AssertRoundtripsAll`) is the second net: it fills every member
  with data, snapshots, restores and compares, catching mistakes static analysis
  cannot see (for example state wrongly marked `[IgnoreSnapshot]`).
- The generator targets **netstandard2.0 and Roslyn 4.8**, so it loads in every build
  host and IDE.

## Consequences

- Adding state to a snapshotted aggregate is a conscious act; the compiler insists.
- Some valid C# patterns (fields, computed properties) need an annotation.
- A generator crash would undermine the guarantee — it surfaces only as warning
  CS8785 — so generator robustness is tested with `GeneratorDriver` tests, and the
  package smoke test fails on CS8785.
