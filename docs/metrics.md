# Metrics and observability

## What is measured

Every store layer reports reads and writes through `IEventStoreMetrics`, tagged
with the **aggregate type** and a **source** that tells you which layer served the
request:

| Source | Emitted by | Read = | Write = |
|---|---|---|---|
| `eventStore` | `CoreEventStore` | full replay (`LoadByIdAsync` reaching the core) | append (`SaveAsync`) |
| `mongodb-events` / `sql-events` | the persistence | event query | event insert |
| `mongodb-snapshots` / `sql-snapshots` | the persistence | snapshot lookup (hit or miss) | snapshot write |
| `redis` | `RedisCacheStore` | **cache hit** (only hits are counted as reads) | cache write (write-through / populate) |

This makes the important ratios directly visible:

- **Cache hit rate** = `redis` reads ÷ (`redis` reads + `eventStore` reads) per
  aggregate type — if it is low for a type you cache, the TTL is too short or the
  access pattern is not repetitive.
- **Snapshot effectiveness** = duration of `eventStore` reads with vs. without
  snapshots (histogram) — if replay still dominates, lower the cadence.
- **Write latency** = `eventStore` write duration (includes projections and sync
  handlers!) vs. `mongodb-events` write duration (the pure append) — a growing gap
  means projections or sync handlers are getting expensive.

## Prometheus

```csharp
es.UsePrometheusMetrics();   // FEB.EventSourcing.Metrics
```

registers `PrometheusEventStoreMetrics` (prometheus-net) which exposes:

| Metric | Type | Labels |
|---|---|---|
| `eventstore_db_reads_total` | counter | `aggregate`, `source` |
| `eventstore_db_writes_total` | counter | `aggregate`, `source` |
| `eventstore_read_duration_seconds` | histogram (exponential buckets 5 ms … ~2.5 s) | `aggregate`, `source` |
| `eventstore_write_duration_seconds` | histogram | `aggregate`, `source` |

Expose them with prometheus-net's usual middleware (`app.UseMetricServer()` /
`MapMetrics()`); the framework only registers the collectors.

Useful PromQL:

```promql
# cache hit rate per aggregate (5m)
sum by (aggregate) (rate(eventstore_db_reads_total{source="redis"}[5m]))
/
sum by (aggregate) (rate(eventstore_db_reads_total{source=~"redis|eventStore"}[5m]))

# p95 command latency (append + projections + sync handlers)
histogram_quantile(0.95, sum by (le, aggregate) (rate(eventstore_write_duration_seconds_bucket{source="eventStore"}[5m])))
```

## Custom sink

Implement `IEventStoreMetrics` (eight small methods: increment/record for read/write,
generic and `Type`-based) and register it as a singleton **before** or instead of
`UsePrometheusMetrics()`:

```csharp
services.AddSingleton<IEventStoreMetrics, OpenTelemetryEventStoreMetrics>();
```

Without any registration a `NoOpEventStoreMetrics` is used — zero overhead.

## Logging

The framework logs through `ILogger<T>` (Microsoft.Extensions.Logging) and is quiet
in the happy path. Messages worth alerting on:

| Level | Source | Message |
|---|---|---|
| Warning | `MongoDbEventStorePersistence` | `Healing … version document at X but last stored event is Y` — a stream was repaired after a crash between version advance and event insert |
| Warning | `RedisCacheStore` | `Redis cache read/write/invalidation … failed` — Redis unreachable or payload unreadable; the store falls back |
| Warning | `OutboxWorker` | `no subscribers registered` |
| Error | `OutboxWorker` | `Outbox delivery to subscriber X failed …` (per attempt) / `loop crashed, backing off` |
| Error | `SnapshotWriteWorker` | `Snapshot write for aggregate X failed` |

## Outbox health

The outbox is observable through its collections/tables rather than metrics
(there is no built-in gauge yet):

- **Backlog:** `db.outbox.countDocuments({ DispatchedAt: null })` — should hover near
  zero; a growing number means a subscriber is slow or down.
- **Per subscriber:** aggregate over `Deliveries.<name>.DispatchedAt == null`.
- **Dead letters:** `db.outbox_deadletter.countDocuments()` grouped by `Subscriber`
  — every increment is an incident to look at.

A small scheduled job that publishes these three numbers as gauges is a good
addition to any production deployment.
