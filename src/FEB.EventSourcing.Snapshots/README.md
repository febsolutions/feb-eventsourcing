# FEB.EventSourcing.Snapshots

Snapshot support for the FEB.EventSourcing framework: the provider-neutral
`SnapshotStore` decorator (snapshot + delta on load, cadence-based writes after save,
storage via the persistence's `ISnapshotPersistence`), `UseSnapshots()` for the
snapshot metadata registry (prefer the explicit
`UseSnapshots(SnapshotMetadataModule_<Assembly>.CreateAll())` overload), and
`UseBackgroundSnapshotWrites()` moves snapshot persistence off the command path
onto a bounded queue drained by a hosted service.

Snapshot contracts and the `[AutoSnapshot]` source generator live in
`FEB.EventSourcing.StateContracts`.
