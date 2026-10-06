# FEB.EventSourcing

[![CI](https://github.com/febsolutions/feb-eventsourcing/actions/workflows/ci.yml/badge.svg)](https://github.com/febsolutions/feb-eventsourcing/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FEB.EventSourcing.svg)](https://www.nuget.org/packages/FEB.EventSourcing)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A modular event sourcing framework for .NET: MongoDB, PostgreSQL or SQL Server
persistence with optimistic concurrency, compile-time generated snapshots
(`[AutoSnapshot]`), an optional version-guarded Redis aggregate cache, projections,
sync and async event handlers, and an outbox with independently tracked subscribers,
written in the same transaction as the events on PostgreSQL, SQL Server and MongoDB
replica sets.

**Documentation:** [docs/README.md](docs/README.md) ·
**Runnable example:** [samples/OrderSample](samples/OrderSample) ·
**Changes between versions:** [upgrade guide](docs/upgrade-guide.md)

## Installation

```bash
dotnet add package FEB.EventSourcing
dotnet add package FEB.EventSourcing.MongoDb      # or .Postgres / .SqlServer / .InMemory
```

Add `FEB.EventSourcing.Snapshots` for snapshots and `FEB.EventSourcing.Redis` for the
state cache; the snapshot source generator is included and needs no extra setup.
The packages target .NET 8.

## Choosing a persistence

Pick exactly one persistence package; everything else (snapshots, Redis cache,
outbox, projections, handlers) works identically on top of it.

| Package | Database | Notes |
|---|---|---|
| `FEB.EventSourcing.MongoDb` | MongoDB 4.4+ (standalone or replica set) | collections per aggregate type; works without transactions (unique index + self-heal), optional transactions on replica sets |
| `FEB.EventSourcing.Postgres` | PostgreSQL 12+ | `FEB.EventSourcing.Sql` + Npgsql; `jsonb`, transactional append, `SKIP LOCKED` outbox lease |
| `FEB.EventSourcing.SqlServer` | SQL Server 2016+ / Azure SQL | `FEB.EventSourcing.Sql` + Microsoft.Data.SqlClient; transactional append, `READPAST` outbox lease |
| `FEB.EventSourcing.InMemory` | — | tests and local development only (no outbox) |

All three database providers pass the same provider contract test suite (events,
concurrency, snapshots, outbox); choose by what your organisation already operates.
Details: [MongoDB](docs/mongodb.md), [SQL](docs/sql.md).

```csharp
services.AddEventSourcing(es =>
    {
        es.UseMongoDb(connectionString, o => o.EnableSnapshots());
        es.UseSnapshots();
        es.UseRedis(redisConnectionString, o => o.SetCachePrefix("myapp"));

        es.Aggregate<Order, string>(a =>
        {
            a.Factory(Order.CreateNew);
            a.Snapshots(s => s.EveryNEvents(50));
        });
    },
    typeof(Order).Assembly);
```

## Repository layout

| Path | Content |
|---|---|
| `src/FEB.EventSourcing.Abstractions`, `src/FEB.EventSourcing` | contracts + core (builder, store chain, projections, outbox worker) |
| `src/FEB.EventSourcing.MongoDb`, `.Sql`, `.Postgres`, `.SqlServer`, `.InMemory` | persistence providers |
| `src/FEB.EventSourcing.StateContracts`, `.Snapshots`, `.Snapshots.Generator` | `[AutoSnapshot]` contracts, snapshot store, source generator |
| `src/FEB.EventSourcing.Redis` | aggregate state cache |
| `src/FEB.EventSourcing.Metrics`, `.TestKit` | Prometheus metrics; snapshot contract assertions for tests |
| `src/FEB.Cqrs` | lightweight command/query dispatcher package |
| `src/FEB.EventSourcing.Tests` | framework test suite (xUnit + Testcontainers, ≥ 85 % coverage gate) |
| `tests/PackageConsumer` | package smoke test: consumes the packed packages like an application |
| `samples/OrderSample` | minimal end-to-end example, source of the documentation snippets |
| `docs/` | user documentation |

## Development

Requirements: the .NET 8 SDK (or newer) and Docker — the integration tests start
MongoDB, Redis, PostgreSQL and SQL Server through Testcontainers.

```bash
dotnet test src/FEB.EventSourcing.Tests      # full suite
./scripts/coverage.sh                        # same, with the 85 % coverage gate
./scripts/package-smoke-test.sh              # pack everything and build a consuming app
dotnet run --project samples/OrderSample     # runnable walkthrough (no infrastructure needed)
```

See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Releases are
published to nuget.org from version tags by the [release workflow](.github/workflows/release.yml).

## Security

Please report vulnerabilities privately — see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © FEB Solutions
