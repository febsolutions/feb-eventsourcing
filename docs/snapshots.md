# Snapshots

## Why snapshots

Loading an aggregate means replaying its events. For a handful of events that is
negligible; for an aggregate with thousands of events (a settings object edited for
years, a long-running account) it becomes the dominant latency of every command.
A **snapshot** is a persisted copy of the aggregate's state at a known version. On
load, the store restores the snapshot and replays only the events *after* it.

Snapshots are a **pure optimization**: they are derived from the events, can be
deleted at any time, and are regenerated automatically. Nothing in the domain
depends on them.

## How the framework does it

Instead of serializing the aggregate object itself (fragile: private state,
collections, framework internals), a **source generator** creates, at compile time,
for every aggregate marked `[AutoSnapshot]`:

- a **snapshot DTO** (`OrderSnapshot`) mirroring exactly the mapped properties,
- `CreateSnapshot()` and `RestoreFromSnapshot(dto)` methods on the aggregate,
- an `ISnapshotMetadata` implementation (`OrderSnapshotMeta`) that knows the types
  and a **schema version** (a hash of the DTO shape),
- a per-assembly registration module `SnapshotMetadataModule_<Assembly>`.

The generator is fail-closed: it refuses to compile if any state could be lost (see
"What gets snapshotted"). The stores only ever deal with the DTO — the same DTO is
what the Redis cache stores.

## Enabling snapshots

Three things, in three places:

**1. Mark the aggregate.**

```csharp
[AutoSnapshot]
public partial class Order : AggregateRoot<Order, string>   // partial!
{
    public string Customer { get; set; } = string.Empty;
    public List<OrderLine> Lines { get; set; } = [];        // OrderLine must be partial too
    ...
}

public partial class OrderLine { ... }
```

Reference `FEB.EventSourcing.StateContracts` from the domain assembly — it brings
the attributes *and* the generator (as an analyzer). The generator also reaches every
project that references any FEB.EventSourcing persistence package, transitively; no
separate generator package exists or is needed.

**2. Enable the store layer and the metadata registry.**

```csharp
es.UseMongoDb(connectionString, o => o.EnableSnapshots());   // or UsePostgres / UseSqlServer
es.UseSnapshots(SnapshotMetadataModule_MyDomain.CreateAll());   // explicit (preferred)
// or: es.UseSnapshots();   // AppDomain scan fallback
```

The explicit form lists exactly the generated metadata of your domain assembly —
deterministic, no reflection scan, no surprises when assemblies load lazily. The
generated module lives in namespace `FEB.EventSourcing.Snapshots.Generated`.

**3. Set a cadence per aggregate.**

```csharp
es.Aggregate<Order, string>(a =>
{
    a.Factory(Order.CreateNew);
    a.Snapshots(s => s.EveryNEvents(50));
});
```

Without `Snapshots(...)` the cadence is 0 = no snapshots for that type, even if
`[AutoSnapshot]` is present (the DTO is still generated and usable, e.g. by Redis).

## What gets snapshotted (the fail-closed rule)

Every member of an `[AutoSnapshot]` type — and of every nested type and every
source-level base class — is exactly one of:

- **mapped** into the DTO,
- **explicitly excluded** with `[IgnoreSnapshot]`,
- a **compile error**.

There is no fourth state. State can never be silently dropped. In detail:

| Member | Handling |
|---|---|
| Property with getter + accessible setter, supported type | mapped |
| Get-only / computed property (`=> …`) | ASG002 error — annotate `[IgnoreSnapshot]` if it is derived, or give it a setter if it is state |
| Property in a base class you wrote (same compilation) | mapped like your own; a `private set` there is unreachable → ASG002 |
| Property in the framework base (`Id`, `Version`) | not mapped — the store restores them itself |
| Instance field | ASG005 error — model as a property or `[IgnoreSnapshot]` |
| Property of unsupported type (`Dictionary<,>`, arrays, external classes) | ASG004 error |
| Static members, indexers | ignored |

`[IgnoreSnapshot]` is a **documented decision**, not a way to make errors go away:
the TestKit lists ignored members in its output so they are reviewed. Typical
legitimate uses: caches, computed values recalculated in `OnAfterRehydrate`,
transient UI state.

`OnAfterRehydrate()` is called after a snapshot restore as well as after a full
replay — use it to recompute anything you excluded.

Supported property types: primitives, `string`, `decimal`, enums, `Guid`,
`DateTime`, `DateTimeOffset` (all also nullable), `List<T>` / `IReadOnlyList<T>` /
`ICollection<T>` of those or of nested partial classes, nested partial classes.
See the [generator reference](generator-reference.md) for the full list and all
diagnostics.

## Cadence

`EveryNEvents(n)` means "take a snapshot whenever a save crosses a multiple of *n*
events". Concretely: with the aggregate at version *b* before the save and *a*
after, a snapshot is taken iff `(a+1)/n > (b+1)/n` (integer division). This
crossing check matters: a single command that writes 5 events at once can jump from
event count 48 to 53 with *n* = 50 — a naive `count % n == 0` would never fire, and
the aggregate would never get a snapshot again.

Choosing *n*:

- Smaller *n* → shorter replay on load, more snapshot writes. Snapshot writes are
  cheap (one upsert of a small document) — err on the small side.
- 20–100 is a good default for aggregates that see continuous change.
- For "settings"-style aggregates that are read on every request and changed
  rarely, `EveryNEvents(1)` is perfectly reasonable: every save refreshes the
  snapshot, every load is snapshot + zero events.
- 0 disables.

## Schema versioning

The generator hashes the snapshot shape — property names and types, **recursively
including nested types** — into an `int` schema version. It is stored on every
snapshot document (`SnapshotVersion`) and every Redis entry (`sv`).

On load, a snapshot whose schema version differs from the current one is treated as
absent → full replay → the next cadence hit writes a fresh snapshot with the new
schema. This means:

- Changing any snapshotted type (adding/removing/renaming a property, changing a
  nested type) invalidates snapshots automatically. No migration, no upcasters.
- The cost is one full replay per aggregate after deployment — for very large
  streams this can be a noticeable latency bump for the first load. If that
  matters, deploy in a quiet window or warm the cache.
- Snapshot schema and **event** schema are independent: events must stay
  backwards-readable (see [concepts](concepts.md#event-design-and-versioning)),
  snapshots may change freely.

## Background writes

By default the snapshot is written synchronously at the end of `SaveAsync`. To
take it off the command path:

```csharp
es.UseBackgroundSnapshotWrites(queueCapacity: 1024);
```

Then `SaveAsync` only *captures* the DTO (synchronously — so later mutations of the
aggregate object cannot leak into the snapshot) and enqueues the write. A hosted
service (`SnapshotWriteWorker`) drains the queue. Semantics:

- Failed writes are logged and dropped; the next cadence hit tries again.
- When the queue is full (`queueCapacity`), new jobs are dropped without blocking.
- On shutdown, jobs still in the queue are lost — harmless (derived data).
- Needs a generic host for the hosted service; in plain console apps resolve and
  start `SnapshotWriteWorker` yourself or stay synchronous.

Use it when snapshot latency is measurable in your p95 (large DTOs, high write
rates); otherwise the synchronous default is simpler.

## Verifying your snapshot contracts

The generator guarantees no member is *forgotten*; it cannot guarantee that a
`RestoreFromSnapshot` round-trip is *correct* for hand-written parts
(`OnAfterRehydrate`, ignored members that were actually state). Add one test per
application:

```csharp
[Fact]
public void All_aggregates_roundtrip_completely()
    => SnapshotContract.AssertRoundtripsAll(typeof(Order).Assembly);
```

See [Testing](testing.md#snapshot-contract-assertions-testkit).

## Interplay with Redis

The Redis cache stores the same DTO, tagged with the same schema version. A
schema change therefore invalidates cache entries as well (they become misses and
expire). Snapshots (in the persistence, durable) and cache (Redis, volatile) are otherwise
independent: you can run either, both, or neither.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Aggregate never gets a snapshot | cadence not set for the type (`a.Snapshots(...)` missing) or `EnableSnapshots()` missing on the persistence options | set both |
| `UseSnapshots()` finds no metadata | assembly not loaded at registration time (scan fallback) | use `UseSnapshots(SnapshotMetadataModule_X.CreateAll())` |
| Generated types missing in the IDE | analyzer cache | reload project / restart IDE (generator targets netstandard2.0 + Roslyn 4.8, loads in all hosts) |
| Full replay after every deployment | expected once per aggregate when the schema hash changed | nothing; or deploy in a quiet window |
| `ASG005` on a field you need | fields are never mapped | make it a property, or `[IgnoreSnapshot]` + recompute in `OnAfterRehydrate` |
