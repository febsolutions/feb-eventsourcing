# Concepts

This chapter is the conceptual reference: how the pieces fit together and what
guarantees you can rely on. Read it once completely; afterwards the per-topic
chapters (MongoDB / SQL persistence, snapshots, Redis, outbox) go deeper where needed.

## Aggregates, events, replay

An **aggregate** is a consistency boundary: a cluster of state that must be changed
transactionally and whose invariants are checked together. In this framework it is
a class deriving from `AggregateRoot<TAggregate, TId>`.

An **event** is an immutable fact that something happened to an aggregate
(`OrderPlaced`, `OrderShipped`). Events implement `IDomainEvent<TAggregate>` and
carry an `ApplyTo(aggregate)` method that mutates state.

**Replay** rebuilds an aggregate from its history: create an empty instance (via the
registered factory), apply every stored event in order. The result is guaranteed to
be the state that existed after the last event — that is the whole point of event
sourcing: the stream is the truth, the object is a cache.

The base class gives you:

| Member | Meaning |
|---|---|
| `Id` | the aggregate id (`TId`) |
| `Version` | index of the last applied event; `-1` for a brand-new aggregate |
| `Raise(event)` | apply + record as uncommitted (use in command methods) |
| `GetUncommittedEvents()` | what `SaveAsync` will persist |
| `Commit(newVersion)` | called by the store after a successful append |
| `Replay(events)` / `RestoreState(version, events)` | used by stores to rehydrate |
| `OnAfterRehydrate()` | virtual hook, called after replay/restore — recompute derived, non-persisted state here |
| `EnsureHasId()` | abstract; validate the id (called on create and rehydrate) |
| `CreateNew(id)` (static) | factory that sets the id and calls `EnsureHasId` |

**Version semantics** are worth memorising because they appear in concurrency,
snapshots and the cache: versions are 0-based event indexes. After saving a batch of
*n* events on a new aggregate, `Version == n - 1`. `LoadEventsAsync(id, fromVersion)`
is *inclusive* of `fromVersion`.

## The event store API

`IEventStore<TAggregate, TId>` is what you inject. Its members:

| Method | What it does | Typical use |
|---|---|---|
| `LoadByIdAsync(id)` | Rehydrates the aggregate (snapshot + delta or full replay; cache first if configured). Returns `null` if it does not exist. | every command that changes an existing aggregate |
| `SaveAsync(aggregate, context)` | Appends the uncommitted events with an expected-version check, then projections, outbox, sync handlers. No-op if nothing is uncommitted. | end of every command |
| `LoadEventsAsync(id, fromVersion)` | The raw event envelopes (payload + metadata) from `fromVersion` (inclusive) on. | audit views, debugging, custom projections, exports |
| `IsExistsAsync(id)` | Cheap existence check (version document / cache), no replay. | validation before creating, guards |
| `GetAllIdsAsync()` | All aggregate ids of this type. Loads them all into memory — fine for thousands, not for millions. | admin tasks, migrations, rebuilds |
| `RebuildProjectionsAsync()` | Replays every aggregate and re-runs projection writers batch by batch. | after changing a read model |
| `UpdateProjectionsAsync(id, context)` | Re-runs projections for one aggregate from its current state (marked obsolete). | one-off repair |

`SaveAsync` needs a `CommandContext`. It is a plain record you fill from your
request pipeline:

| Field | Ends up as | Purpose |
|---|---|---|
| `CommandId` | `CausationId` on every event | which command produced the event; also groups events into batches on rebuild |
| `CorrelationId` | `CorrelationId` on every event, handlers, outbox | trace a business transaction across services |
| `TenantId`, `UserId` | metadata | multi-tenancy, audit |
| `Headers` | metadata | free-form (e.g. client version, request id) |
| `ReceivedAt` | `ProjectionContext.ReceivedAt` | when the command arrived |

Build it once per request in middleware or a small factory; do not create ad-hoc
contexts with random ids in business code — the ids are what makes tracing work.

## The store chain (decorators)

`IEventStore<TAggregate, TId>` is implemented as a **chain of decorators** around a
core store. Every layer either adds a capability or is transparent:

```
EventStore (outer facade)
  └─ RedisCacheStore        (optional: es.UseRedis)          Order = EventStoreLayer.Cache (200)
      └─ SnapshotStore        (optional: o.EnableSnapshots)   Order = EventStoreLayer.Snapshots (100)
          └─ CoreEventStore   (always)                        Order = EventStoreLayer.Core (0)
```

- **CoreEventStore** — replay, append with concurrency check, projections, outbox
  enqueue, sync dispatch. Everything correctness-relevant lives here.
- **SnapshotStore** — provider-neutral; on load, tries a snapshot and replays only
  the delta; after save, may write a new snapshot (cadence). Storage is delegated to
  the persistence's `ISnapshotPersistence` (MongoDB, SQL). Transparent for aggregates
  without `[AutoSnapshot]`.
- **RedisCacheStore** — serves the current state from Redis when present, populates
  the cache on miss, writes through on save, invalidates on conflict. Also
  transparent for aggregates without `[AutoSnapshot]`.

The **position** of each layer is defined by `IEventStoreRegistration.Order`, not by
the order of `Use*()` calls. The builder sorts ascending (stable for equal values).
Well-known values are in `EventStoreLayer`: `Core = 0`, `Snapshots = 100`,
`Cache = 200`, `CrossCutting = 300`. This guarantees, for instance, that the cache
always sits *outside* the snapshot layer (a cache hit must skip persistence
entirely) — regardless of how you ordered your configuration lines.

**Custom layers.** Anything cross-cutting — tracing, auditing, tenant checks, rate
limiting — is a decorator. Implement `IEventStoreRegistration` (pick an `Order`,
usually `EventStoreLayer.CrossCutting`), derive your store from
`ProxyStore<TAggregate, TId>` (which forwards everything to `InnerStore`), override
what you need, and add it via `es.EventStoreChainBuilder.AddDecorator(new MyRegistration())`.
The registration is called once per registered aggregate type. See the test
`ChainOrderTests.Custom_layer_can_slot_in_at_any_order` for a complete minimal
example.

## Optimistic concurrency

`SaveAsync` passes the aggregate's current `Version` as the *expected version* to
the persistence. The persistence advances the stored version with an atomic
compare-and-set; if another writer got there first, it throws
`ConcurrencyException<TId>` (carrying `AggregateId`, `ExpectedVersion` and, where
known, `ActualVersion`) and **nothing is written** — no events, no projections, no
outbox entries.

This is the **single correctness anchor** of the framework. Caches and snapshots
are optimizations that may be stale; a stale writer always fails at the append and
can reload and retry. Design consequences:

- Load, mutate, save **within one command**. Do not hold an aggregate across
  requests.
- On `ConcurrencyException`, the correct reaction is almost always: reload, re-apply
  the command's intent, save again — in a loop with a small retry budget. Whether
  the retry is safe depends on the command's semantics (adding a line is
  re-appliable; "set total to X" may not be).
- Concurrent *creation* of the same new id is also a concurrency conflict (exactly
  one writer wins).

```csharp
for (var attempt = 0; ; attempt++)
{
    var order = await store.LoadByIdAsync(id, ct) ?? throw new NotFoundException(id);
    order.AddLine(article, qty, price);
    try
    {
        await store.SaveAsync(order, context, ct);
        return;
    }
    catch (ConcurrencyException<string>) when (attempt < 3)
    {
        // someone else changed the order — reload and try again
    }
}
```

## Projections

Projections build **read models** — denormalized, query-optimized views. The
framework calls them synchronously after the events are persisted, inside
`SaveAsync`:

- **`IAggregateProjectionWriter<TAggregate>.UpdateAsync(aggregate, context, ct)`**
  gets the up-to-date aggregate. Ideal for "current state" documents: overwrite the
  read-model row from the aggregate's properties. Simple, always consistent with the
  aggregate, does not care which events happened.
- **`IEventProjectionWriter<TAggregate, TId>.ApplyAsync(id, newEvents, context, ct)`**
  gets only the events of this save. Ideal for append-style read models (activity
  feeds, statistics) or when a projection needs to *react to* specific events rather
  than mirror state.

Both are discovered by the assembly scan and registered as **scoped** services, so
they can take scoped dependencies (a DbContext, a MongoDB collection wrapper, an SQL
connection factory). One
save calls every writer that matches the aggregate type — you can have several.

**Consistency model.** Because projections run in the same call as the append,
your read models are updated before `SaveAsync` returns — a subsequent query sees
the new state (read-your-writes within the process). The price: projections must be
fast, and a *throwing* projection fails the command *after* the events are already
durable. See "Failure handling" below.

**Rebuilding.** `RebuildProjectionsAsync()` iterates all aggregate ids, replays each
from scratch and calls the writers **once per original command batch** (events are
grouped by `CausationId`), so event-projection writers see the same batches they saw
originally. It runs sequentially and loads each aggregate's stream fully; for large
stores plan a maintenance window and make writers idempotent (a rebuild
overwrites). It runs at the core store level, i.e. it does *not* invalidate Redis
cache entries — cache and read models are independent anyway.

## Event handlers: sync vs. async

There are two hook points for "when event X happens, do Y", with very different
guarantees:

| | `ISyncEventHandler<TEvent>` | `IASyncEventHandler<TEvent>` |
|---|---|---|
| Runs | inside `SaveAsync`, after append + projections | later, by the outbox worker |
| Failure | propagates to the caller — the command fails (events already persisted!) | retried, then dead-lettered; command unaffected |
| Latency | adds to the command | none on the command path |
| Ordering | in save order, in-process | at-least-once, no global order |
| Requires | nothing | `UseMongoOutbox()` or `UseSqlOutbox()` + `UseOutboxWorker()` |
| Use for | cheap, must-happen-now, in-process reactions (invalidate a local cache, append to an in-memory feed) | anything talking to the outside world or that may fail |

Both receive an `EventHandlingContext` with the event's metadata
(`EventId`, `AggregateType`, `Version`, `OccurredAt`, tenant/user, correlation,
causation, headers) and `GetAggregateId<TId>()`.

Rule of thumb: **if it can fail for reasons outside your process, it belongs in an
async handler (or a dedicated outbox subscriber).** Details, guarantees and
multi-consumer setups are in [Outbox and subscriptions](outbox.md).

## Event design and versioning

Events are the one thing you cannot refactor freely: they are stored forever and
must stay readable. A few rules make this painless:

1. **Additive changes only.** Adding an optional property to an event is safe
   (`SetIgnoreExtraElements` is enabled; missing properties default). Removing or
   renaming a property, or changing its type, breaks reading old events.
2. **When the shape must change, add a new event type** (`OrderPlacedV2`) and keep
   the old one applying correctly. Aggregates simply implement `ApplyTo` for both.
   Do not try to "upcast" in place; explicit versions are easier to reason about
   and to test.
3. **Give events a stable storage name** if you ever expect to move or rename the
   class — see the next section. Without one, the CLR type name is what ends up in
   your data, and the class is pinned by it.
4. **Keep events small and intention-revealing.** `OrderLineAdded(article, qty,
   price)` — not `OrderChanged(newState)`. Fat "state dump" events make projections
   and consumers depend on your entire aggregate shape.
5. **Never validate or throw in `ApplyTo`.** History has already happened.
6. **Prefer records with positional constructors** — both the MongoDB driver and
   System.Text.Json (SQL, outbox) map them without ceremony, and immutability is what
   you want.

Snapshots are *not* affected by event versioning: they are derived data and are
regenerated whenever their own schema changes.

### Stable storage names: `[EventName]`

By default every persistence stores the event's CLR type name alongside the payload
(MongoDB: BSON discriminator `_t`, the class's short name; SQL and the outbox: the
assembly-qualified name). That ties your stored data to the class: moving the event
to another namespace or project, or renaming it, makes old events unreadable.

`[EventName]` decouples the two — the stored name becomes yours to choose, and the
class can move freely:

```csharp
[EventName("order.placed")]
public sealed record OrderPlaced(string Customer) : IDomainEvent<Order>;
```

From then on `order.placed` is what sits in the store and in outbox payloads, and the
class can be moved, renamed or split out into its own contracts assembly without
touching the data. Rules of thumb:

- **The attribute is optional.** Events without it behave exactly as before, so
  existing applications keep working unchanged and can adopt it event by event.
- **Pick a name that is not a CLR name** — `order.placed`, `invoice.booked` — and
  treat it as part of your data: never change it again.
- **Adopting it on an existing event needs no migration.** The class's short name
  stays readable on MongoDB automatically, and SQL/outbox values written earlier are
  still interpreted as CLR type names. Only when the class has *already* moved do you
  need to say so explicitly:

```csharp
[EventName("order.placed", Aliases = ["Old.Namespace.OrderPlacedV1, Old.Assembly"])]
public sealed record OrderPlaced(string Customer) : IDomainEvent<Order>;
```

  Aliases are read-only: they resolve old stored values, nothing is written under
  them. List the exact strings your data contains — the assembly-qualified name for
  SQL/outbox rows, the plain class name for MongoDB documents.
- **Names must be unique.** Two events claiming the same name (or alias) fail at
  startup rather than corrupting reads later.
- Name resolution uses the assemblies you pass to `AddEventSourcing`, like every
  other registration. An event whose assembly is not listed cannot be resolved by
  name; the error message says so.

For the symmetric problem on the aggregate side — collection and `aggregate_type`
names are derived from the aggregate's CLR type name — there is no attribute: treat
aggregate class names as stable (see [mongodb.md](mongodb.md#collections)).

## Failure handling

Where things can go wrong in a command, and what the framework does:

| Failure point | Effect | Your reaction |
|---|---|---|
| Aggregate method throws (invariant violated) | nothing persisted | return a domain error |
| Append conflicts (`ConcurrencyException<TId>`) | nothing persisted | reload + retry, or surface the conflict |
| Persistence unavailable | nothing persisted (exception) | fail the request; retry policy at the edge |
| Projection writer throws | **events are already durable**; the command fails; outbox/sync handlers not run for this save | make projections robust (catch transient errors internally, log, continue) or accept a rebuild; treat as a bug |
| Sync handler throws | events + projections done; the command fails | keep sync handlers trivial or move the work to an async handler |
| Snapshot write fails | logged, ignored (or dropped from the background queue) | nothing — the next cadence hit retries |
| Redis unavailable | logged, falls back to the store | nothing — watch the logs / metrics |
| Outbox subscriber throws | retried per subscriber, then dead-lettered | inspect the dead-letter collection |

The uncomfortable case is a throwing projection: the events are committed but the
caller sees an error. There is no distributed transaction that could roll the
events back — that is inherent to event sourcing. Design projections so that
transient failures are handled *inside* the writer, and treat a projection failure
as an operational incident (fix, then `RebuildProjectionsAsync` for the affected
type). If a read model is allowed to lag, build it from an async handler instead —
then a failure never touches the command.

## Metadata

Every persisted event carries an `EventMetadata<TId>`:

| Field | Source | Notes |
|---|---|---|
| `EventId` | generated | globally unique; idempotency key for consumers |
| `AggregateId`, `AggregateType` | aggregate | type name only (not assembly-qualified) |
| `Version` | store | position in the stream |
| `OccurredAt` | store (`UtcNow` at save) | |
| `TenantId`, `UserId` | `CommandContext` | |
| `CorrelationId` | `CommandContext` | required |
| `CausationId` | `CommandContext.CommandId` | groups events of one command |
| `Headers` | `CommandContext` | free-form dictionary |

The same data reaches projections as `ProjectionContext`, handlers as
`EventHandlingContext`, and outbox subscribers as `OutboxMetadata`.

## Multi-tenancy

The framework is tenant-agnostic by design: `TenantId` is carried as metadata on
every event, but nothing filters or partitions by it. Typical patterns:

- **Tenant in the aggregate id** (`"{tenant}:{id}"`) — simplest; every load is
  naturally scoped.
- **Tenant as a property + check in command methods** — the aggregate refuses
  operations for the wrong tenant.
- **Database per tenant** — MongoDB: provide your own `IMongoDbContext`
  (`UseMongoDb<MyTenantContext>()`) that resolves the database per request; SQL: a
  schema per tenant (`o.SetSchema(...)`) or a connection string per tenant via your
  own dialect wrapper.

Projections receive `TenantId` in the `ProjectionContext` and should key their read
models by it.
