# 0016 — Relational stores can join a transaction provided by the caller

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

Since [0015](0015-outbox-written-atomically-with-the-events.md) the relational
persistences write the version check, the events and the outbox envelopes in one
transaction. That transaction is opened and committed by the persistence itself, on a
connection it creates. Projections run afterwards, outside of it.

This is not enough when event sourcing is introduced **into an existing application**
whose tables stay in place:

- The application already writes its tables, typically through an ORM (EF Core), inside
  its own transaction. During a shadow phase it keeps doing so and additionally records
  events for the same change. If the events and the table write commit separately, the
  two can diverge for technical reasons (crash, timeout, failing second write), and a
  reconciliation between table and projection can no longer tell technical gaps from
  real modelling errors.
- After the switch-over the existing tables *are* projections. They live in the same
  database as the events. If the projection runs after the events have been committed,
  a failing projection leaves a stored event whose table row was never written (the
  consequence accepted in 0015). For tables that the application reads directly this is
  not acceptable.
- A `TransactionScope` does not help: the persistence opens its own connection and
  calls `BeginTransaction` itself, and two connections in one scope escalate to a
  distributed transaction.

All required writes live in **one database**. A single local transaction can cover them,
provided the store can use the caller's connection and transaction.

## Decision

### 1. A scoped unit of work

A provider-neutral contract in `FEB.EventSourcing.Abstractions`, so that layers which
know nothing about SQL (Redis cache, snapshots) can defer their side effects:

```csharp
public interface IUnitOfWorkContext
{
    bool IsActive { get; }

    // Runs the action after the unit of work has committed; discarded on rollback.
    // Without an active unit of work the action runs immediately.
    Task AfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);
}
```

and the relational unit of work in `FEB.EventSourcing.Sql`:

```csharp
public interface ISqlUnitOfWork : IUnitOfWorkContext
{
    DbConnection Connection { get; }      // throws if not active
    DbTransaction Transaction { get; }    // throws if not active

    // Join a transaction the caller already owns (e.g. EF Core). The caller commits.
    void Join(DbConnection connection, DbTransaction transaction);

    // Called by the owner of the transaction once it has committed / rolled back.
    Task CompletedAsync(CancellationToken cancellationToken = default);
    void Discarded();
}
```

**Registration — optional without being absent.** `UsePostgres`/`UseSqlServer` always
register `SqlUnitOfWork` as a scoped service (as `ISqlUnitOfWork` and
`IUnitOfWorkContext`). It is **inactive** until `Join` is called; an application that
never joins behaves exactly as before, and an application that does can decide per
call. The persistences receive it through an **additional constructor**; the existing
public constructors stay unchanged and use an inactive unit of work, so neither source
nor binary compatibility breaks. Where no relational provider is configured, no
`IUnitOfWorkContext` is registered and the layers behave as without a unit of work.

**`Join` validates** that the transaction belongs to the given open connection, that no
other unit of work is active in the scope (no nesting), and that the connection targets
the configured database (database name). Otherwise it throws — events must never land
silently in a different database.

**Ownership.** A unit of work belongs to the flow that joined it. Anything that runs
later or concurrently — background snapshot writes, `AfterCommitAsync` actions — runs
only after `CompletedAsync`, when the unit is no longer active, and opens its own
connections. ADO.NET connections are not thread-safe, so the caller's connection is
never shared with background work.

Usage with EF Core (same `Microsoft.Data.SqlClient` connection as the SQL Server
dialect, so it can be shared directly):

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
uow.Join(db.Database.GetDbConnection(), tx.GetDbTransaction());
try
{
    await db.SaveChangesAsync(ct);                 // existing table write
    await handler.HandleAsync(command, ct);        // load → aggregate → SaveAsync
    await tx.CommitAsync(ct);
    await uow.CompletedAsync(ct);                  // runs the AfterCommitAsync actions
}
catch
{
    await tx.RollbackAsync(ct);
    uow.Discarded();
    throw;
}
```

A small helper (`ExecuteInUnitOfWorkAsync`) may wrap this pattern; it is not required.

### 2. Behaviour of the relational persistence when a unit of work is active

- **Append** (`AppendEventsAsync`, `AppendEventsWithOutboxAsync`): uses
  `Connection`/`Transaction` of the unit of work. It does **not** open, commit, roll back
  or dispose anything. Version compare-and-set, events and envelopes are written as
  today.
- **Concurrency conflict:** throw `ConcurrencyException<TId>` **without** rolling back.
  The transaction belongs to the caller; the caller rolls back the whole unit of work
  (table write included) and may retry.
- **Conflicts under stricter isolation levels:** because the caller's isolation level
  applies, a concurrent writer can surface as a database error instead of an update of
  zero rows — serialization failure (PostgreSQL `40001`), snapshot update conflict
  (SQL Server `3960`), deadlock victim (`40P01` / `1205`). These are reported as
  `ConcurrencyException<TId>` as well, inside and outside a unit of work, so the
  caller's retry logic applies. Dialects recognise them (`ISqlDialect.IsConcurrencyConflict`,
  a default-implemented member).
- **Reads** (`LoadEventsAsync`, `IsExistsAsync`, `GetAllIdsAsync`, snapshot loads) also
  use the unit of work's connection and transaction. Reading through a second connection
  would not see the unit's own uncommitted events and, on SQL Server without row
  versioning, would block on the rows locked by the unit itself (self-deadlock).
- **Outbox:** written in the same transaction, as in 0015.
- **Isolation level:** the caller's. The persistence must not change it; correctness
  rests on the version compare-and-set ([0001](0001-optimistic-concurrency-is-the-correctness-anchor.md)),
  not on isolation.

Without an active unit of work everything behaves exactly as today.

### 3. Projections inside the transaction

`DefaultProjectionUpdater` is unchanged — projections already run inside
`SaveAsync`. What changes is that they can now take part in the transaction:

- Projection writers may inject `ISqlUnitOfWork` and write through
  `Connection`/`Transaction` (ADO.NET, Dapper).
- Projection writers may inject the application's scoped `DbContext` when the unit of
  work was joined from that context — it is already enlisted.
- If a projection throws, the exception propagates; the caller rolls back, so neither
  the table write nor the events nor the envelopes nor any projection row remain. The
  "stored but not projected" state of 0015 cannot occur inside a unit of work.

### 4. Everything with an effect outside the transaction runs after commit

Inside a unit of work `SaveAsync` returns **before** the data is committed. Layers that
touch anything outside the database must defer to `AfterCommitAsync`:

- **Redis state cache** (`RedisCacheStore`): the write-through after a save runs via
  `AfterCommitAsync`; on rollback nothing is cached. Cache-aside fills on loads are
  skipped while a unit of work is active, because such a load can see the unit's own
  uncommitted events. Invalidation stays immediate — removing an entry is always safe.
- **Snapshots:** inline snapshot writes **must** run inside the transaction (the
  relational snapshot persistence uses the unit's connection). A snapshot written
  through a separate connection would survive the caller's rollback — a snapshot of a
  state that never existed, with a version the stream does not have. Background snapshot
  writes are enqueued via `AfterCommitAsync`.
- **Synchronous event handlers** (`ISyncEventHandler`): keep running inside
  `SaveAsync` (they are documented as in-process and cheap), but the documentation must
  state that inside a unit of work they run **before** the commit and must not have
  external side effects. Anything external belongs in the outbox.
- **Metrics:** may be recorded immediately.

### 5. The aggregate instance after a rollback

`SaveAsync` commits the aggregate in memory (`aggregate.Commit(nextVersion)`) before
the caller commits. After a rollback that instance is stale and must be discarded and
reloaded. This is documented; no mechanism is added.

### 6. Optional: a self-managed unit of work

Without a caller transaction, the store may open one itself that also covers the
projections (events, envelopes and projections in one commit), by activating the unit
of work internally around `SaveAsync`. This is a separate, opt-in step
(`o.ProjectionsInTransaction()`), not part of the first implementation.

## Consequences

- Existing applications can record events **in the same transaction** as their current
  table writes. A reconciliation between existing tables and projections then only
  reports real modelling differences.
- After switch-over, tables that are projections are always consistent with the events.
- Concurrency conflicts that an existing application silently resolved by "last writer
  wins" now surface as `ConcurrencyException` and fail the caller's transaction. Callers
  need a retry strategy (reload, recompute, retry). This is intended.
- A bug in the event-sourced part fails the caller's write. Applications introducing
  event sourcing into a running system should be able to switch the event part off by
  configuration during the shadow phase.
- Longer transactions: event, envelope and projection writes now hold the caller's
  locks longer. Batch operations should use one unit of work per aggregate.
- Only relational providers support this. MongoDB is out of scope.
- Envelopes get their creation time when they are inserted but become visible to the
  outbox worker only at commit. With long transactions, delivery order **across**
  aggregates can therefore deviate more from creation order; within one aggregate it
  stays correct, because the version check serializes its writers
  ([0010](0010-outbox-retention-and-ordering.md)).
- Rejected: `TransactionScope`/ambient transactions (escalation to distributed
  transactions, explicit `BeginTransaction` in the persistence); an ambient unit of work
  (static `AsyncLocal`) — it would flow into background work started inside the unit
  and let it use the caller's connection concurrently; an optional parameter on the
  existing constructors (binary break); letting the caller pass connection and
  transaction as `SaveAsync` parameters (would change `IEventStore` and every layer of
  the store chain, and reads would still use a second connection).

## Tests (provider contract, PostgreSQL and SQL Server via Testcontainers)

1. Caller commits: table row, events, version, envelopes and projection rows exist.
2. Caller rolls back after `SaveAsync`: none of them exist.
3. A projection throws: after the caller's rollback none of them exist.
4. Concurrency conflict inside a unit of work: `ConcurrencyException` is thrown, the
   caller's transaction is still usable for rollback, nothing is written; a retry with
   a reloaded aggregate succeeds.
5. Two `SaveAsync` calls on the same aggregate in one unit of work: the second load sees
   the first one's events; no deadlock.
6. The persistence does not close, commit or dispose the caller's connection or
   transaction.
7. The caller's isolation level is kept.
8. Redis cache and background snapshots are written only after `CompletedAsync`, never
   after `Discarded`.
9. Without a unit of work the existing provider contract suite passes unchanged.
10. EF Core integration: `db.Database.BeginTransactionAsync` + `Join` + `SaveChangesAsync`
    + `SaveAsync` + a projection that writes through the same `DbContext` commit and roll
    back together.
11. An inline snapshot written inside a unit of work does not survive the caller's
    rollback.
12. A concurrent writer under a stricter isolation level (PostgreSQL `REPEATABLE READ`,
    SQL Server `SNAPSHOT`) surfaces as `ConcurrencyException`.
13. `Join` rejects a connection to a different database and a second, nested join.
