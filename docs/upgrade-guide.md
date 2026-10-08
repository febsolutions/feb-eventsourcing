# Upgrade guide

## 9.4.0

No action is required. The packages now contain assets for **.NET 8 and .NET 10**
(`lib/net8.0`, `lib/net10.0`); NuGet picks the matching one. Applications on .NET 8 are
unaffected. Removing .NET 8 will be a major release with its own entry here
([decision 0017](decisions/0017-target-frameworks.md)).

## 9.3.1

No code changes. `FEB.EventSourcing.MongoDb` now depends on **MongoDB.Driver 3.12.0**
(previously 3.5.2). The older driver pulled in `Snappier` 1.0.0 and `SharpCompress`
0.30.1, both with published security advisories
([GHSA-pggp-6c3x-2xmx](https://github.com/advisories/GHSA-pggp-6c3x-2xmx),
[GHSA-6c8g-7p36-r338](https://github.com/advisories/GHSA-6c8g-7p36-r338)); 3.12.0
references fixed versions. If your application references `MongoDB.Driver` directly
with a lower version, raise it to at least 3.12.0 — otherwise NuGet reports a package
downgrade (NU1605).

## 9.3.0

No action is required to upgrade. Two changes are worth knowing:

1. **Database-level conflicts are reported as `ConcurrencyException<TId>`** by the
   PostgreSQL and SQL Server providers: serialization failures (`40001`), snapshot update
   conflicts (`3960`) and deadlock victims (`40P01` / `1205`), with the provider exception
   as `InnerException`. Previously the provider exception surfaced directly. If you caught
   `PostgresException`/`SqlException` for these cases, catch `ConcurrencyException<TId>`
   instead — it is the same retry situation as a version conflict. Custom `ISqlDialect`
   implementations can recognise such errors by implementing `IsConcurrencyConflict`.
2. **Outbox envelopes are written with the events** — in the same transaction on
   PostgreSQL, SQL Server and MongoDB with `UseTransactions()`, and always before the
   projections. A failing projection no longer keeps stored events from their subscribers
   ([decision 0015](decisions/0015-outbox-written-atomically-with-the-events.md)).

New and optional: relational stores can join a transaction owned by the application —
see [SQL → unit of work](sql.md#joining-the-callers-transaction-unit-of-work).

## 9.2.1 (first open-source release)

The framework is now developed in the open at
[github.com/febsolutions/feb-eventsourcing](https://github.com/febsolutions/feb-eventsourcing)
under the MIT license, and every release is published to nuget.org. Package IDs and
namespaces are unchanged. 9.2.1 is functionally 9.2.0 plus two fixes:

1. **The snapshot source generator now ships with the packages.** Up to 9.2.0 the
   generator was not contained in any package: applications that consumed the
   framework from a NuGet feed and used `[AutoSnapshot]` got no generated snapshot
   code (`SnapshotMetadataModule_<Assembly>` did not exist, and `UseSnapshots()` found
   no metadata, so snapshots silently stayed off). It now ships inside
   `FEB.EventSourcing.StateContracts` (`analyzers/dotnet/cs`) and reaches every project
   that references a FEB.EventSourcing package, transitively. **What to expect when
   upgrading:** the generator runs for the first time in such applications. Because it
   is fail-closed, aggregates whose state cannot be snapshotted now produce generator
   diagnostics at build time — mostly errors such as ASG002 (property without a
   setter) or ASG005 (instance field); resolve them as described in the
   [generator reference](generator-reference.md). Afterwards snapshots are written
   according to the configured cadence; correctness never depended on them.
2. **Aggregates in the global namespace** (no `namespace` declaration, e.g. in a
   top-level program) no longer crash the generator. Previously this surfaced only as
   warning CS8785 and produced no snapshot code.

Also new: `FEB.Cqrs` and `FEB.EventSourcing.InMemory` now declare the MIT license in
their package metadata (it was missing), and all packages ship symbol packages and
Source Link, so debuggers can step into the exact sources.

## 9.0 – 9.2 (snapshot/Redis redesign and follow-ups)

This release fixes several latent defects and restructures the snapshot/Redis
packages. Functional fixes (no action needed, listed for awareness):

- Snapshot delta load no longer double-applies the event at the snapshot version.
- Snapshot cadence uses a crossing check — multi-event commands can no longer skip
  a snapshot forever.
- Scanned `ISyncEventHandler<T>` / `IASyncEventHandler<T>` /
  `IAggregateProjectionWriter<T>` implementations are now actually registered under
  their generic interfaces and get invoked. **If your app relied on scanned
  handlers never firing, they will fire now.**
- Outbox payloads now contain the real event data (previously `{}`).
- `InMemoryEventStorePersistence` throws `ConcurrencyException<TId>` (previously
  `InvalidOperationException`).
- `FEB.Cqrs`: the non-generic `ICommandDispatcher.ExecuteAsync(ICommand)` overload
  threw at runtime (wrong generic arity); it now resolves `ICommandHandler<TCommand>`
  for the runtime type. Commands with results should be dispatched via the explicit
  `ExecuteAsync<TResult>(cmd)` / `ExecuteAsync<TCommand, TResult>(cmd)` overloads (see
  [cqrs.md](cqrs.md)).

### Breaking changes

1. **New package `FEB.EventSourcing.StateContracts`.** The snapshot contracts
   (`ISnapshotMetadata`, registry, `ISnapshotSerializer`, `SnapshotCompression`,
   `[AutoSnapshot]`, `[IgnoreSnapshot]`) moved there — *type names and the
   `FEB.EventSourcing.Snapshots` namespace are unchanged*, so this is a package
   reference change only. The source generator now ships with StateContracts.
2. **`UseSnapshots(Action<SnapshotOptions>)` removed.** The options callback was a
   no-op. Use `UseSnapshots()` or the new `UseSnapshots(metadata)` overload;
   cadence is configured per aggregate via `a.Snapshots(s => s.EveryNEvents(n))`.
3. **Redis cache storage format changed** (hash with CAS instead of JSON envelope).
   Old cache entries are simply never read again and expire via TTL. Use a new
   `CachePrefix` if you want to clean up eagerly. `Newtonsoft.Json` is no longer a
   dependency.
4. **Snapshot schema hashes changed** (now recursive over nested types). Existing
   MongoDB snapshots are ignored after deployment → one full replay per aggregate,
   then snapshots are rebuilt on the next cadence hit.
5. **EventStore V1→V2 migration removed** (`EventStoreCheckAndRunMigrationsAsync`,
   `EnableMigrations`, migration persistence). The migration was a one-time legacy
   step that has long been applied everywhere.
6. **Generator hosting**: the generator now targets `netstandard2.0`/Roslyn 4.8 and
   is fail-closed — aggregates with instance fields or unmapped base-class state
   that previously compiled may now produce ASG002/ASG005 errors. Annotate
   intentionally transient members with `[IgnoreSnapshot]`.

7. **`GetAllIdsAsync` returns `IReadOnlyList<TId>`** instead of `List<TId>` (on
   `IEventStore<,>` and `IEventStorePersistence`). Callers that only enumerate are
   unaffected; call `.ToList()` where a mutable list is needed.
8. **`ProxyStore` inner store is non-nullable**; custom decorators that passed `null`
   must pass the real inner store.
9. **`IEventStoreRegistration` gained `int Order`** (see `EventStoreLayer`). Custom
   registrations must implement it. Chain order is now determined by `Order`, not by
   `Use*()` call order — Redis always ends up outside snapshots.
10. **`OutboxWorker` is now hosted via `es.UseOutboxWorker(...)`** and no longer
    constructed manually (constructor changed to `IServiceScopeFactory` +
    options). `OutboxWorkerOptions` is registered by that call.
11. **`EventStoreBuilder` (legacy) and `Metrics.AddPrometheusMetrics(EventStoreBuilder)`
    removed** — use `es.UsePrometheusMetrics()`.
12. **`IEventSourcingBuilder` gained `ApplicationAssemblies` and `RegisteredAggregates`**;
    custom builder implementations must provide them.
13. **Event BSON class maps** are now registered from the explicit `AddEventSourcing`
    assemblies instead of an AppDomain scan — list every assembly that contains events.
14. **A unique index `ux_aggregate_version`** is created on every `*Events` collection at
    startup. If existing data contains duplicate `(AggregateId, Version)` pairs (should
    not happen), index creation fails and is logged; clean the data or disable with
    `o.DisableIndexInitialization()`.

15. **Snapshot store is provider-neutral.** `MongoDbSnapshotStore`,
    `MongoDbSnapshotStoreRegistration` and `IMongoSnapshotStorePersistence` are gone;
    the decorator is now `FEB.EventSourcing.Snapshots.SnapshotStore` (registered
    automatically by `EnableSnapshots()` on any persistence) and the storage contract
    is `FEB.EventSourcing.Snapshots.ISnapshotPersistence` (returns `StoredSnapshot?`).
    Only affects code that referenced these types directly.
16. **New persistence providers**: `FEB.EventSourcing.Postgres` and
    `FEB.EventSourcing.SqlServer` (both on `FEB.EventSourcing.Sql`) with
    `UsePostgres(...)`, `UseSqlServer(...)`, `UseSqlOutbox()`. See [sql.md](sql.md).
17. **Outbox subscriptions.** `IOutboxPersistence` is now subscriber-aware
    (`DequeueBatchAsync(subscriberNames, …)`, `MarkDispatchedAsync/MarkFailedAsync`
    take a subscriber name); `IOutboxDispatcher` is superseded by the named
    `IOutboxSubscriber` (`UseOutboxDispatcher<T>` still works as a single-subscriber
    adapter). Existing envelopes need no migration. Dead-letter ids are now
    `"{EventId}:{subscriber}"`. See [outbox.md](outbox.md).

18. **Outbox dequeue index changed (9.0.0-beta.2).** The previous
    index could not serve the dequeue's sort; with a large backlog every lease
    degraded to a full scan. MongoDB replaces the old `ix_dequeue` automatically at
    startup; Postgres/SQL Server drop `ix_outbox_dequeue` and create the filtered
    `ix_outbox_pending` via the schema initializer. No manual action — but if you
    added a custom workaround index on `(DispatchedAt, CreatedAt)`, remove it after
    updating (it is now redundant).

19. **Outbox retention (9.0.0-beta.3).** Completed envelopes are now removed after
    **7 days by default** (MongoDB: TTL index `ttl_dispatched`; SQL: periodic bounded
    delete by the worker). Previously they were kept forever. To keep the old
    behavior call `DisableCompletedCleanup()` on the outbox options. Dead letters are
    never cleaned up. Custom `IOutboxPersistence` implementations gain a default
    no-op `CleanupCompletedAsync`; custom `ISqlDialect` implementations must add
    `DeleteCompletedOutbox`.

20. **MongoDB append hardening + optional transactions (9.1.0).**
    The self-heal now reconciles the version document with the stored events in
    *both* directions (previously only "version ahead" was repaired — a version
    document *behind* the events, caused by a rollback after an insert that had in
    fact committed server-side, blocked the stream permanently). Streams stuck in
    that state repair themselves on the next full load after updating. A
    duplicate-key during append is no longer blindly treated as an idempotent
    retry: events are compared by `EventId`, and a foreign event at one of the
    claimed versions now raises `ConcurrencyException<TId>` instead of silently
    discarding the new events. `ConcurrencyException<TId>` from the MongoDB version
    check now carries `ActualVersion`. New opt-in `o.UseTransactions()` on
    `UseMongoDb` runs version check + event insert in one multi-document
    transaction (requires a replica set; a single-node replica set is enough). The
    `MongoDbEventStorePersistence` constructor gained an optional
    `MongoEventStoreOptions` parameter (relevant only if you construct it manually).

21. **Stable event names (9.2.0, additive).** Events can now carry
    `[EventName("order.placed")]`, which replaces the CLR type name in the store and
    in outbox payloads and lets the class move between namespaces and projects. The
    attribute is **optional**: without it nothing changes, so no action is required
    on upgrade. Adopting it on an existing event needs no data migration — the
    previous CLR name stays readable (MongoDB: as an additional discriminator; SQL
    and outbox: as the fallback interpretation of unknown names) — use
    `Aliases = [...]` only for names that no longer match the current class. Custom
    outbox subscribers should resolve types via
    `EventTypeNames.ResolveRequired(envelope.Payload.EventType)` instead of
    `Type.GetType`. Duplicate names across event types now fail fast at startup. See
    [concepts → stable storage names](concepts.md#stable-storage-names-eventname).

### Recommended follow-ups

- Give your events stable names with `[EventName]` before the first move becomes
  urgent — adopting it later on already-moved classes needs explicit aliases.
- Register the outbox pump: `es.UseOutboxWorker()` (previously nothing dispatched the
  outbox unless you hosted `OutboxWorker` yourself).

- Register snapshot metadata explicitly:
  `es.UseSnapshots(SnapshotMetadataModule_<Assembly>.CreateAll())`.
- Add the TestKit contract test to every app with `[AutoSnapshot]` aggregates.
- Consider `es.UseBackgroundSnapshotWrites()` to take snapshot writes off the
  command path.
