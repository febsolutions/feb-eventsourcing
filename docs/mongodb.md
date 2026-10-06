# MongoDB persistence

`FEB.EventSourcing.MongoDb` is the production persistence: event streams, snapshot
storage and the outbox. It is designed to run on a **standalone** MongoDB server —
no replica set and no multi-document transactions are required (see "Atomicity"
below for how consistency is achieved without them).

## Setup

```csharp
es.UseMongoDb("mongodb://localhost:27017/orders", o =>
{
    o.EnableSnapshots();                            // adds the snapshot store layer
    o.SetEventStoreCollectionPrefix("EventStore");  // default; collection name prefix
    o.SetCompression(SnapshotCompression.None);     // snapshot payload compression
    o.DisableIndexInitialization();                 // only if you manage indexes yourself
    o.UseTransactions();                            // opt-in, requires a replica set (see Atomicity)
});
```

- The connection string **must** contain the database name (`/orders`).
- Alternatively bring your own context: `es.UseMongoDb<MyMongoDbContext>(o => ...)`
  with a class implementing `IMongoDbContext` (`Client`, `Database`,
  `GetCollection<T>(name)`). Use this for tenant-per-database setups or when the
  application already owns a `MongoClient` you want to share (recommended — one
  `MongoClient` per process).
- `UseMongoDb` must be called **before** `UseRedis`; the builder throws otherwise.

## Collections

Per aggregate type `Foo` (default prefix `EventStore`):

| Collection | One document per… | Content |
|---|---|---|
| `EventStore.FooEvents` | event | `_id` (event id), `Metadata` (aggregate id/type, version, timestamps, tenant, user, correlation, causation, headers), `Payload` (the event as BSON with type discriminator) |
| `EventStore.FooVersions` | aggregate | `_id` (aggregate id), `Version` — the optimistic-concurrency anchor |
| `EventStore.FooSnapshots` | aggregate | latest snapshot: `Payload` (bytes), `EventStreamVersion`, `SnapshotVersion` (schema hash), `Compression`, `CreatedUtc` |
| `outbox` | outbox envelope | see [outbox.md](outbox.md) |
| `outbox_deadletter` | failed subscription | see [outbox.md](outbox.md) |

Collection names are built by `EventStoreCollectionNameBuilder` from the prefix
and the aggregate's CLR type name — renaming an aggregate class renames its
collections (i.e. orphans the old data). Treat aggregate class names as stable.

## Atomicity without a replica set

Appending a batch touches two collections (versions, events). Without transactions
the store guarantees consistency like this:

1. **Version compare-and-set.** `FindOneAndUpdate` on the version document with
   filter `{ _id: id, Version: expected }` and update `{ Version: expected + n }`
   (upsert when `expected == -1`). This is atomic on one document. If no document
   matches, someone else appended first → `ConcurrencyException<TId>`. Two writers
   *creating* the same id race on the upsert; the loser hits the `_id` duplicate
   and also gets `ConcurrencyException<TId>`.
2. **Event insert.** The events are inserted with `InsertMany` (ordered). The unique
   index on `(Metadata.AggregateId, Metadata.Version)` guarantees that no version
   can be stored twice. On a duplicate-key error the store checks **per version by
   `EventId`** whether the existing document is the same event: then it is a retry
   after a partial insert, and only the missing documents are added. If a *foreign*
   event occupies one of the versions, the append fails with
   `ConcurrencyException<TId>` — it is never silently acknowledged.
3. **Failure between the two.** If the insert throws, the version document is
   rolled back (best effort). Whatever divergence remains — process died between
   the steps, or the insert was in fact committed server-side and only the client
   saw an error (timeout, replica-set election) — is repaired on the **next full
   load**: the store reconciles the version document with the events actually
   stored, in *both* directions (ahead → rolled back, behind → advanced, missing →
   recreated; logged as `Healing …`). Stored events are the truth: a write the
   server committed has happened even if the caller got an error, exactly like any
   database write whose acknowledgment is lost. Subsequent saves succeed again. No
   manual repair, no stuck streams.

On a concurrency conflict the exception carries the version the store currently
claims (`ActualVersion`, also part of the message) — worth surfacing in your API
error handling (map `ConcurrencyException<TId>` to HTTP 409, ideally with one
reload-and-retry before giving up).

### Transactions (opt-in, requires a replica set)

```csharp
es.UseMongoDb(connectionString, o =>
{
    o.UseTransactions();   // version check + event insert in one transaction
});
```

When the deployment runs a replica set — a **single-node replica set is
sufficient** — `UseTransactions()` executes the version compare-and-set and the
event insert of every append in one multi-document transaction. No intermediate
state can be observed or left behind, whatever fails in between; the self-heal
described above remains active but becomes a pure safety net (it still repairs
states left over from before the switch). With `UseMongoOutbox()` the outbox
envelopes are part of the same transaction, so a stored event always has its envelope.
On a standalone server this option makes every append fail — it is strictly opt-in and
the two-step protocol stays the default.

**The outbox without a replica set:** the envelopes are written immediately after the
events, in one batch, and before the projections — but not atomically with them. If
the process dies between the two writes, or the outbox write fails, those events are
stored without envelopes and are not delivered. This is an accepted limitation of
running without a replica set (see [outbox.md](outbox.md#1-why-an-outbox-at-all)).

What neither mode gives you: atomicity across *different* aggregates. One
`SaveAsync` = one aggregate = one batch. If a use case must change two aggregates
atomically, that is a domain-model smell (merge them, or use a process manager with
compensating actions).

## Indexes

`UseMongoDb` registers `MongoDbIndexInitializer` as a hosted service. On application
start it creates, idempotently, for every aggregate registered via `es.Aggregate<,>()`:

| Collection | Index | Why |
|---|---|---|
| `*Events` | `ux_aggregate_version` — `(Metadata.AggregateId, Metadata.Version)`, **unique** | fast loads (every load is a range scan on this index) and the duplicate protection above |
| `outbox` | `ix_dequeue` — `(DispatchedAt, CreatedAt)` | lease-based dequeue: equality on `DispatchedAt = null` + index-backed sort by `CreatedAt`; the lease check on `LockedUntil` is filtered residually (a `LockedUntil` column inside the index would break the sort and degrade every lease to a blocking sort over the backlog) |

Notes:

- The initializer runs when the host starts (`IHostedService.StartAsync`). In
  processes without a generic host (plain console app, tests) call
  `MongoDbIndexInitializer.EnsureAllAsync()` yourself, or resolve the hosted
  service and start it.
- Aggregates registered *after* startup (dynamically) are not covered — register
  all aggregates in `AddEventSourcing`.
- If existing data violates the unique index (duplicate `(AggregateId, Version)`
  pairs — which cannot happen with this framework but might with hand-imported
  data), index creation fails and the error is logged; fix the data or disable
  initialization.
- Snapshot and version collections use `_id` only — no extra index needed.
- Upgrading from 9.0.0-beta.1: the old 3-column `ix_dequeue` is detected and replaced
  automatically at startup.
- `ttl_dispatched` — TTL index on `DispatchedAt` enforcing the completed-envelope
  retention (default 7 days; `SetCompletedRetention` / `DisableCompletedCleanup`).
  A changed retention migrates the index automatically.

## Event serialization

Payloads are stored as BSON documents produced by the MongoDB driver's class maps.
On `UseMongoDb`, a class map is registered for every `IEvent` implementation found
in the assemblies passed to `AddEventSourcing(..., assemblies)`, with `AutoMap()`
and `SetIgnoreExtraElements(true)`. The type is identified by the discriminator
`Payload._t`: the `[EventName]` name when the event has one, otherwise the class's
short name. Adopting `[EventName]` later needs no migration — the short name and any
declared aliases stay registered as additional read-only discriminators.
Consequences:

- **Adding** a property to an event is safe: old documents deserialize with the
  default value.
- **Removing** a property is safe for *reading* (extra elements are ignored) but
  obviously loses the information going forward — usually you want a new event
  type instead.
- **Renaming** a property is a breaking change for stored data. Renaming or moving
  the *class* is fine if it carries `[EventName]` (see
  [concepts → stable storage names](concepts.md#stable-storage-names-eventname));
  without it the class's short name is the discriminator and therefore fixed.
- Records with positional constructors work out of the box; `Guid` is stored in
  standard representation; `object`-typed members are allowed
  (`ObjectSerializer.AllAllowedTypes`).
- Assemblies containing events **must** be listed in `AddEventSourcing` — an event
  type without class map falls back to driver defaults, which usually still works
  but without `IgnoreExtraElements`.

## Snapshots and compression

Snapshot payloads are BSON-serialized snapshot DTOs, optionally compressed with LZ4
(`K4os.Compression.LZ4`, fast) or Zstd (`ZstdSharp`, smaller). Compression is set
per store (`o.SetCompression(...)`) and recorded on each snapshot document, so
changing it later is safe — old snapshots are read with the algorithm they were
written with. Start with `None`; enable compression when snapshot documents get
into the hundreds of KB.

## Outbox

```csharp
es.UseMongoOutbox(o =>
{
    o.SetOutboxCollectionName("outbox");
    o.SetDeadLetterCollectionName("outbox_deadletter");
    o.SetLeaseDuration(TimeSpan.FromMinutes(5));   // how long a worker "owns" an envelope
    o.SetMaxAttempts(10);                           // per subscriber
});
es.UseOutboxWorker(o =>
{
    o.SetBatchSize(50);
    o.SetPollingInterval(TimeSpan.FromSeconds(1));
});
```

The outbox document model, the lease mechanics and per-subscriber delivery
tracking are described in [Outbox and subscriptions](outbox.md).

## Operations

**Rebuild read models.** `IEventStore<T,TId>.RebuildProjectionsAsync()` replays
every aggregate of the type and re-runs projection writers batch by batch. It is
sequential and loads each stream fully — for large stores schedule it in a
maintenance window and make writers idempotent. Rebuild does not touch the Redis
cache (independent concern).

**Backups.** Everything is in the collections above; a normal MongoDB backup is a
complete backup. Snapshots and cache entries are derived and can be discarded.

**Deleting snapshots** (e.g. after a bug in a `[AutoSnapshot]` aggregate) is always
safe: drop the `*Snapshots` collection, the next load replays fully and the next
cadence hit writes a fresh snapshot.

**Schema changes** to snapshotted aggregates need nothing: the schema hash changes,
old snapshots are ignored, replay happens once per aggregate, new snapshots are
written on the next cadence hit. Expect a short latency bump after deployment.

**Growth.** Event collections only grow. There is no built-in archiving; if a
stream becomes truly huge (millions of events for one aggregate), the aggregate
boundary is probably too coarse. Snapshots keep *load* time constant, but backups
and index size grow linearly.

**Standalone vs. replica set.** The store works on both. On a replica set you gain
durability (`w: majority`) — configure it in the connection string; the framework
does not force a write concern.
