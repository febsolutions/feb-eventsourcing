# 0012 — Relational providers share one core and one table set

- **Status:** Accepted
- **Date:** 2026-08-15

## Context

Many organisations operate relational databases rather than MongoDB, and adopting the
framework should not require new infrastructure. PostgreSQL and SQL Server cover most
of them. Relational databases offer real transactions, which removes the two-step
problem described in [0007](0007-mongodb-without-replica-set.md).

## Decision

- A shared core, `FEB.EventSourcing.Sql`, over plain ADO.NET; database specifics live in
  a small `ISqlDialect` (connections, DDL, upsert, the outbox lease query, duplicate-key
  detection, JSON casts). `FEB.EventSourcing.Postgres` and `FEB.EventSourcing.SqlServer`
  are dialects.
- **One table set for all aggregate types** (`events`, `aggregate_versions`,
  `snapshots`, `outbox`, `outbox_deadletter`), discriminated by `aggregate_type`, instead
  of tables per type — DDL per aggregate type is awkward to migrate and grant.
- **Append is one transaction:** version compare-and-set plus event inserts.
- The outbox lease uses `FOR UPDATE SKIP LOCKED` / `READPAST`, so parallel workers never
  process the same envelope.
- Every provider must pass the same **provider contract test suite** as MongoDB.
- MySQL is not implemented; it would be another dialect and has to pass the same
  contract tests.

## Consequences

- PostgreSQL and SQL Server are full peers of MongoDB (events, snapshots, outbox).
- New databases are added as dialects, not as new persistence implementations.
