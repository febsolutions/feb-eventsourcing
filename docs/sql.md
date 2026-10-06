# SQL persistence (PostgreSQL, SQL Server)

`FEB.EventSourcing.Sql` is the relational persistence core; `FEB.EventSourcing.Postgres`
and `FEB.EventSourcing.SqlServer` add the database-specific dialect. Functionally it
is a full peer of the MongoDB persistence — events, snapshots, outbox — and passes the
same provider contract test suite. Choose it when your organisation runs relational
databases; nothing else in the framework changes.

## Setup

```csharp
// PostgreSQL (Npgsql)
es.UsePostgres("Host=localhost;Database=orders;Username=app;Password=…", o =>
{
    o.EnableSnapshots();
    o.SetSchema("es");                              // default "es"
    o.SetCompression(SnapshotCompression.None);
    o.DisableSchemaInitialization();                // only if DDL is managed externally
});

// SQL Server (Microsoft.Data.SqlClient)
es.UseSqlServer("Server=localhost;Database=orders;User Id=app;Password=…;TrustServerCertificate=true", o =>
{
    o.EnableSnapshots();
});

// relational outbox (either provider)
es.UseSqlOutbox(o =>
{
    o.SetMaxAttempts(10);
    o.SetLeaseDuration(TimeSpan.FromMinutes(5));
});
es.UseOutboxWorker();
```

Everything else — `UseSnapshots`, `UseRedis`, `UseOutboxWorker`, subscribers,
`Aggregate<,>` registration — is identical to the MongoDB setup.

## Tables

One set of tables serves **all** aggregate types (discriminated by `aggregate_type`),
in the configured schema (default `es`):

| Table | Purpose | Key |
|---|---|---|
| `events` | one row per event: `event_id`, `aggregate_type`, `aggregate_id`, `version`, `event_type` (assembly-qualified CLR name), `payload` (JSON), `occurred_at`, tenant/user/correlation/causation, `headers` (JSON) | PK `event_id`; **unique** `(aggregate_type, aggregate_id, version)` |
| `aggregate_versions` | one row per aggregate: current version (concurrency anchor) | PK `(aggregate_type, aggregate_id)` |
| `snapshots` | latest snapshot per aggregate: `stream_version`, `schema_version`, `payload` (bytes), `compression`, `created_utc` | PK `(aggregate_type, aggregate_id)` |
| `outbox` | envelopes with `deliveries_json` (per-subscriber state), `dispatched_at`, `locked_until` | PK `id`; partial/filtered index `ix_outbox_pending` on `(created_at) INCLUDE (locked_until) WHERE dispatched_at IS NULL` — contains only pending envelopes and serves the dequeue's sort |
| `outbox_deadletter` | one row per failed subscription | PK `"{event_id}:{subscriber}"` |

Column types per dialect: Postgres uses `uuid`, `jsonb`, `bytea`, `timestamptz`; SQL
Server uses `uniqueidentifier`, `nvarchar(max)` for JSON, `varbinary(max)`,
`datetime2`. On SQL Server the unique `(aggregate_type, aggregate_id, version)` index
is the **clustered** index, so a stream's events are physically contiguous.

Why one table set instead of tables per aggregate type (as MongoDB does with
collections): relational DDL per type is awkward to manage (migrations, grants), and
the composite unique index gives the same guarantees. Aggregate ids are stored as
`varchar(200)` — supported id types are `string`, `Guid`, `int`, `long`.

## Atomicity

The relational providers use a **real transaction** for every append: version
compare-and-set (`UPDATE … WHERE version = @expected`, or an insert-if-absent for new
aggregates), all event inserts and — with `UseSqlOutbox()` — the outbox envelopes
commit or roll back together. There is no
partial-write window and no self-heal needed — the concerns described for MongoDB
standalone do not apply here. `ConcurrencyException<TId>` is raised when the version
update affects 0 rows or the unique event index rejects a duplicate.

Isolation level is `ReadCommitted`; the concurrency guarantee comes from the
row-level compare-and-set, not from isolation.

## Schema initialization

`UsePostgres`/`UseSqlServer` register `SqlSchemaInitializer` as a hosted service that
runs the dialect's idempotent DDL (`CREATE … IF NOT EXISTS` / `IF OBJECT_ID(...) IS
NULL`) at startup: schema, tables, indexes. Options:

- `o.DisableSchemaInitialization()` when DBAs own the DDL — the statements are the
  dialect's `GetSchemaStatements(options)`; you can print them once and hand them
  over.
- `SqlSchemaInitializer.EnsureAsync()` can be called manually (console apps, tests).
- The database itself must exist; the schema is created if missing.
- Required permissions at startup: `CREATE SCHEMA/TABLE/INDEX` in the target
  database (or none, if initialization is disabled).

## Event serialization

Events are stored as JSON via System.Text.Json (default options). `event_type` holds
the event's `[EventName]` name when it has one, otherwise — as before the attribute
existed — its assembly-qualified CLR name. The same rules as elsewhere apply:
additive changes are safe, removed properties are ignored on read, renamed properties
break reading old rows (see
[concepts → event design](concepts.md#event-design-and-versioning)). Moving an event
class to another namespace or assembly is only safe if it carries `[EventName]`;
otherwise the stored CLR name must stay resolvable (type forwarding) or the rows need
a migration. Rows written before the attribute was introduced keep working: a value
that no event claims by name is still interpreted as a CLR type name.

Snapshot payloads are JSON as well (camelCase), optionally LZ4/Zstd-compressed, stored
as bytes.

## Outbox

Same model as the MongoDB outbox — see [Outbox and subscriptions](outbox.md) for
semantics. Provider specifics:

- Envelopes are written **in the append transaction** (see Atomicity): a stored event
  always has its envelope, and a failed outbox write rolls the events back.
- Leasing uses `FOR UPDATE SKIP LOCKED` (Postgres) / `READPAST, UPDLOCK, ROWLOCK`
  (SQL Server): concurrent workers never process the same envelope, and a lease is a
  single statement.
- Per-subscriber state lives in `deliveries_json` and is updated read-modify-write
  inside a short transaction while the envelope is leased.
- Dead letters go to `outbox_deadletter` with one row per `(event, subscriber)`.
- Completed envelopes are removed by the worker's periodic bounded delete after the
  configured retention (default 7 days; `SetCompletedRetention`,
  `DisableCompletedCleanup`, `SetCleanupBatchSize`).

## Operations

- **Backups**: standard database backups; snapshots are derived and disposable.
- **Rebuilding read models**: `RebuildProjectionsAsync()` as everywhere.
- **Growth**: `events` only grows; the clustered/unique index keeps loads O(stream
  length). Partitioning by `aggregate_type` or date is possible with plain SQL if
  ever needed — the framework does not depend on physical layout.
- **Connection handling**: one connection per operation via the ADO.NET pool; pass a
  connection string with pooling enabled (default in both drivers).
- **Metrics**: sources `sql-events` and `sql-snapshots` (see [Metrics](metrics.md)).

## Writing another dialect

`ISqlDialect` is small on purpose: connection factory, DDL statements,
insert-version-if-absent, snapshot upsert, outbox lease query, duplicate-key
detection and two JSON casting hooks. A MySQL 8 dialect, for example, would map
`INSERT IGNORE`, `ON DUPLICATE KEY UPDATE`, `SKIP LOCKED` and error 1062 — and use
`es.UseSql(new MySqlDialect(), connectionString, ...)`. Run the provider contract
tests (`PersistenceContractTests`) against it before shipping.
