# 0005 — Explicit registration, no AppDomain scans

- **Status:** Accepted
- **Date:** 2026-08-14

## Context

Discovering handlers, projections, event types or snapshot metadata by scanning
`AppDomain.CurrentDomain.GetAssemblies()` depends on which assemblies the runtime has
loaded at that moment. The result differs between hosts, test runners and start-up
orders, and a missing assembly fails silently.

## Decision

Registration works only from **explicitly named assemblies and modules**:

- `AddEventSourcing(configure, assemblies...)` receives the application assemblies;
  handlers, projection writers, BSON class maps and stored event names are discovered
  there and nowhere else.
- Snapshot metadata is registered from the generated, per-assembly module
  `SnapshotMetadataModule_<Assembly>.CreateAll()`.

## Consequences

- Behaviour is deterministic across hosts.
- Forgetting to pass an assembly is the typical configuration mistake; error messages
  (for example when an event name cannot be resolved) point to it.
