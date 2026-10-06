# FEB.EventSourcing.Sql

Relational persistence core for the FEB.EventSourcing framework over plain ADO.NET:
event streams (transactional append with optimistic concurrency), snapshots and
the outbox with per-subscriber delivery tracking — events and outbox envelopes are
written in one transaction.
Optionally the store joins a transaction owned by the application (`ISqlUnitOfWork`, e.g.
an EF Core transaction), so the application's own table writes commit together with the
events. Database specifics live in a
small `ISqlDialect`; use it through `FEB.EventSourcing.Postgres` or
`FEB.EventSourcing.SqlServer` — or bring your own dialect via `es.UseSql(dialect, …)`.

Documentation: see the `docs/` folder in the repository (`sql.md`).
