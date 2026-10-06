# Redis aggregate cache

## What it is — and what it is not

The Redis layer caches the **current state** of `[AutoSnapshot]` aggregates so
that `LoadByIdAsync` can be answered from Redis without touching the persistence at
all. It
is a *state cache*, not an event cache and not a read model:

- It caches the same snapshot DTO the snapshot store uses, tagged with the stream
  version and the schema version.
- It is volatile: entries expire (TTL), can be flushed any time, and are never the
  source of truth.
- It does not replace projections. Queries that list or search aggregates still
  belong in read models; the cache only speeds up "give me aggregate X".

## When it pays off

Redis helps when the same aggregates are loaded frequently — typically:

- list/detail views that re-load aggregates instead of reading a projection,
- "settings"- or "tenant"-style aggregates read on every request,
- command-heavy aggregates where every command starts with a load.

It does *not* help for write-once/read-rarely aggregates, and it costs one Redis
round trip on every miss. Measure with the metrics (`redis` vs. `eventStore` reads,
see [Metrics](metrics.md)) before and after enabling it for a type.

## Setup

```csharp
es.UseMongoDb(connectionString, o => o.EnableSnapshots()); // persistence first (or UsePostgres / UseSqlServer)
es.UseSnapshots(SnapshotMetadataModule_MyDomain.CreateAll());
es.UseRedis("localhost:6379", o =>
{
    o.SetCachePrefix("myapp");                    // key namespace, default "eventStore"
    o.SetTtl(TimeSpan.FromMinutes(30));           // default 30 min
    o.SetTtlJitter(0.1);                          // ±10 % (default) against synchronized expiry
    o.SetCompression(SnapshotCompression.None);   // Lz4 / Zstd for large states
});
```

Requirements and behaviour:

- Only aggregates with `[AutoSnapshot]` are cached; others pass through the layer
  untouched. `UseSnapshots(...)` must be registered (it provides the metadata).
- The Redis connection string goes to `StackExchange.Redis`
  (`ConnectionMultiplexer.Connect`); one multiplexer per process is created.
- The cache prefix separates applications (or environments) sharing one Redis.
  Changing it is the cheapest way to "flush" — old keys expire on their own.

## Data layout

One Redis **hash** per aggregate: key `"{prefix}:{AggregateType}:{id}"`, fields:

| Field | Content |
|---|---|
| `v` | stream version of the cached state |
| `sv` | snapshot schema version (generator hash) |
| `p` | payload: the snapshot DTO as System.Text.Json (optionally compressed) |
| `c` | compression algorithm |
| `t` | created-at (unix ms) |

No CLR type names are stored anywhere; the target type comes from the snapshot
metadata registry. The whole entry expires with the key's TTL. You can inspect it
with `HGETALL myapp:Order:order-1`.

## Consistency model

This is the part to understand before enabling the cache in production.

**Write-through with compare-and-set.** After every successful `SaveAsync` the new
state is written to Redis — but only through a Lua script that sets the hash *iff*
the incoming stream version is higher than the stored one. Concurrent writers (or a
late write from a slow instance) can therefore never overwrite newer state with
older state. Losing this race is not an error; the newer state is already there.

**Cache-aside on miss.** When `LoadByIdAsync` finds no entry (or a schema-version
mismatch, or unreadable payload), it loads from the inner store and populates the
cache — again through the same version-guarded write.

**Invalidation on conflict.** When `SaveAsync` fails with
`ConcurrencyException<TId>`, the cache key is deleted before the exception
propagates. A writer that worked on a stale entry can therefore not cause that
entry to be served again; the next load repopulates from the truth.

**Correctness anchor.** The persistence layer's optimistic concurrency. Consider
the worst case: instance A saves version 5, its cache write fails silently (Redis
hiccup); instance B loads version 4 from the (now stale) cache, changes it, saves
with expected version 4 → `ConcurrencyException`, cache invalidated, B reloads
version 5 and retries. Correctness never depended on the cache. The only cost of a
stale entry is one extra round.

**Staleness window.** The only way to serve stale state is a *failed cache write
after a successful save*, and it lasts at most one TTL — or until the next
successful save/load of that aggregate. With write-through in every instance and a
shared Redis this is rare; the TTL is the safety net.

**Graceful degradation.** Every Redis failure (connect, read, write, delete) is
caught, logged at warning level, and the call falls back to the inner store. A
Redis outage slows the application down to "no cache" — it never breaks it. When
Redis comes back, entries repopulate on the next loads.

### Inside a caller-owned transaction

With a relational store, the application can let the event store join its own
transaction ([SQL → unit of work](sql.md#joining-the-callers-transaction-unit-of-work)).
While such a unit of work is active the cache is **bypassed for loads** — the flow may
already have written uncommitted events for the aggregate, and a state loaded through the
transaction could still be rolled back. The write-through after a save is captured at
save time but written only **after the caller has committed**; on rollback nothing is
cached.

## Multiple application instances

All instances must share the **same** Redis (and prefix) — that is what makes
write-through keep everyone's cache warm. Instance-local Redis servers would each
see only their own writes and serve stale state to each other; the version guard
would still keep things *correct*, but the hit rate would suffer badly.

## Schema changes

The schema version `sv` is the generator's hash of the snapshot DTO shape. After
deploying a change to a snapshotted type, existing entries have the old `sv` and
are treated as misses; they expire on their own. No flush, no migration. Old and
new instances running side by side during a rolling deployment write entries with
different `sv` values into the same key — the newer version wins on the version
guard, and each instance ignores entries with a foreign `sv`. Safe.

## Operational notes

- **Memory.** One hash per cached aggregate; size ≈ DTO JSON. Enable `Lz4` when
  entries are tens of KB; the version guard and TTL are unaffected.
- **TTL.** 30 minutes default. Longer TTL = higher hit rate and longer worst-case
  staleness after a failed write. Jitter (default ±10 %) prevents thundering
  re-population when many keys were written at the same moment.
- **Metrics.** Cache hits appear as reads with source `redis`; misses show up as
  reads with source `eventStore` (see [Metrics](metrics.md)).
- **Flushing.** Never necessary. If you want a clean slate anyway: change the
  prefix, or `SCAN`+`DEL` the prefix — the application keeps working while you do.
- **`IsExistsAsync`** returns `true` when a key exists (any schema version) and
  falls back to the store otherwise — cheap existence checks stay cheap.
- **Redis Cluster** is not tested; keys carry no hash tags, so Lua scripts on a
  single key work but multi-key operations (none used today) would not.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| No `redis` reads in metrics | aggregate lacks `[AutoSnapshot]`, `UseSnapshots` missing, or Redis unreachable (see warnings) | check registration; check logs |
| Hit rate low | TTL too short for the access pattern, or loads are not repetitive | raise TTL; verify the type is actually hot |
| Warnings `Redis cache read failed … falling back` | Redis down / network / bad payload | fix Redis; the app keeps running |
| Stale state visible for a while | failed cache write after save (rare) | bounded by TTL; check Redis health |
