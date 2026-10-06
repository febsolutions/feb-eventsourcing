# FEB.EventSourcing.StateContracts

Dependency-free aggregate state contracts for the FEB.EventSourcing framework:
`[AutoSnapshot]` / `[IgnoreSnapshot]`, `ISnapshotMetadata` + registry,
`ISnapshotSerializer` and `SnapshotCompression` — shared by the snapshot store and
the Redis aggregate cache. Ships with the source generator that emits snapshot DTOs,
`CreateSnapshot`/`RestoreFromSnapshot` methods and a per-assembly registration
module for every `[AutoSnapshot]` aggregate at compile time.

The generator is fail-closed: every member of a snapshotted type is either mapped,
explicitly `[IgnoreSnapshot]`, or a compile error — state is never lost silently.
