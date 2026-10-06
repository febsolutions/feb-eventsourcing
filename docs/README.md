# FEB.EventSourcing Documentation

A modular event sourcing framework for .NET: MongoDB, PostgreSQL or SQL Server
persistence, source-generated
snapshots, an optional Redis aggregate cache, projections, sync/async event
handlers and an outbox with independently tracked subscribers (written in the same
transaction as the events on PostgreSQL, SQL Server and MongoDB replica sets).

## User guide

Read in order the first time; each chapter is self-contained afterwards.

| # | Chapter | What you learn |
|---|---|---|
| 1 | [Getting started](getting-started.md) | packages, your first aggregate + events, wiring, save/load, a projection — with the *why* behind each step |
| 2 | [Concepts](concepts.md) | the store API, the decorator chain and layer order, optimistic concurrency and retry, projections, sync vs. async handlers, event design & versioning incl. stable `[EventName]` storage names, failure handling, metadata, multi-tenancy |
| 3 | [MongoDB persistence](mongodb.md) | collections, atomicity without a replica set, indexes, event serialization, operations |
| 3b | [SQL persistence](sql.md) | PostgreSQL and SQL Server: tables, transactional append, schema initialization, dialects |
| 4 | [Snapshots](snapshots.md) | why, `[AutoSnapshot]`, the fail-closed rule, cadence, schema versioning, background writes, troubleshooting |
| 5 | [Redis aggregate cache](redis-cache.md) | when it pays off, data layout, the full consistency model, multi-instance, schema changes, operations |
| 6 | [Outbox and subscriptions](outbox.md) | why an outbox, the three roles, one/several/broker subscribers, delivery guarantees, operations, checklist |
| 7 | [Metrics and observability](metrics.md) | what is measured per layer, Prometheus setup and queries, log messages to alert on, outbox health |
| 8 | [FEB.Cqrs](cqrs.md) | the minimal command/query dispatcher and how it composes with the store |
| 9 | [Generator reference](generator-reference.md) | what is generated, supported types, all ASG diagnostics with examples |
| 10 | [Testing](testing.md) | aggregate unit tests, InMemory store, TestKit contract assertions, Testcontainers, outbox subscribers |
| 11 | [Upgrade guide](upgrade-guide.md) | breaking changes per version and recommended follow-ups |
| 12 | [Architecture decisions](decisions/README.md) | why the framework is built the way it is: each guardrail with its context, the decision and its consequences |

## Choosing a persistence

Exactly one persistence package per application; snapshots, Redis cache, outbox,
projections and handlers behave the same on all of them.

| Package | Database | When to choose it |
|---|---|---|
| `FEB.EventSourcing.MongoDb` | MongoDB 4.4+ (standalone or replica set) | you already run MongoDB; document-native BSON events; collections per aggregate type. Works without a replica set (atomicity via unique index + self-heal). |
| `FEB.EventSourcing.Postgres` | PostgreSQL 12+ | you run PostgreSQL or want a relational store; `jsonb` events, one table set for all aggregates, real transactions on append |
| `FEB.EventSourcing.SqlServer` | SQL Server 2016+ / Azure SQL | your organisation is on SQL Server; same feature set as Postgres |
| `FEB.EventSourcing.InMemory` | — | tests and local development (full concurrency semantics, no outbox) |

Feature parity is verified by the provider contract test suite (see
[Testing](testing.md#6-test-coverage)); each database provider is a full peer —
snapshots, outbox subscriptions and the Redis cache work on all of them.

A complete runnable example lives in [`samples/OrderSample`](../samples/OrderSample)
(`dotnet run`, no infrastructure needed) — all snippets in this documentation are
taken from it or from the framework's own test suite.
