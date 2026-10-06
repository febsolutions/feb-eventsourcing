# Contributing

Thanks for considering a contribution. FEB.EventSourcing runs in production
applications, so changes are held to a few firm rules — they keep the framework
correct and its documentation trustworthy.

## Getting started

Requirements: the .NET 8 SDK (or newer) and Docker. The integration tests start
MongoDB (as a single-node replica set), Redis, PostgreSQL and SQL Server through
Testcontainers; nothing else needs to be installed.

```bash
dotnet build FEB.EventSourcing.sln
dotnet test src/FEB.EventSourcing.Tests      # full suite
./scripts/coverage.sh                        # with the coverage gate (as in CI)
./scripts/package-smoke-test.sh              # pack and consume the packages (as in CI)
```

To run the integration tests against servers you already have, point the fixtures at
them with `ES_TEST_MONGO`, `ES_TEST_REDIS`, `ES_TEST_POSTGRES` and
`ES_TEST_SQLSERVER` (see `src/FEB.EventSourcing.Tests/Infrastructure`).

## Pull requests

- Open an issue first for anything beyond a small fix, so the approach can be agreed
  before you invest time.
- Branch from `main`, keep the change focused, and open the pull request against
  `main`. CI must be green.
- Write commit messages that say *what* changed and *why*.
- All code, comments, tests and documentation are in English.
- Never include customer data, internal hostnames, credentials or other non-public
  information — not in code, tests, sample data or commit messages.

## Rules for every change

**Tests.** No framework code without a test. A bug fix starts with a test that
reproduces the bug, then the fix. Persistence behaviour belongs in the provider
contract tests (`PersistenceContractTests`), which run against every provider. The
suite must stay at or above **85 % line coverage** across the framework packages; when
you change framework code, update the numbers in
[docs/testing.md § 6](docs/testing.md#6-test-coverage).

**Documentation is part of the change.** Every change to public API, behaviour or
configuration updates the documentation in the same pull request:

| Area | Chapter |
|---|---|
| store API, chain, concurrency, projections, handlers, events | `docs/concepts.md` |
| MongoDB, indexes, atomicity, collections | `docs/mongodb.md` |
| PostgreSQL / SQL Server | `docs/sql.md` |
| snapshots, generator behaviour, cadence | `docs/snapshots.md`, `docs/generator-reference.md` |
| Redis cache | `docs/redis-cache.md` |
| outbox, subscribers | `docs/outbox.md` |
| metrics, logging | `docs/metrics.md` |
| FEB.Cqrs | `docs/cqrs.md` |
| testing | `docs/testing.md` |

Breaking changes (signatures, registration API, data layout, behaviour with migration
consequences) additionally get a numbered entry in `docs/upgrade-guide.md`.
A new architectural decision — or a change to an existing one — gets a document in
`docs/decisions/` (see its README for the format). Package
READMEs and XML documentation on public APIs are kept current as well. Code snippets in
the documentation come from `samples/OrderSample` or the test suite — never code that
is not compiled anywhere.

## Architecture guardrails

These are deliberate design decisions; each links to the
[architecture decision](docs/decisions/README.md) that explains it. Changing one needs
a discussion in an issue first and, if accepted, a new decision document that
supersedes the old one.

- **Optimistic concurrency is the only correctness anchor.** Caches and snapshots are
  optimizations and may be stale; everything must stay correct without them.
  ([0001](docs/decisions/0001-optimistic-concurrency-is-the-correctness-anchor.md))
- **No replica set required.** MongoDB code must work on standalone servers;
  consistency comes from the unique index plus self-healing. Multi-document
  transactions are strictly opt-in (`UseTransactions()`).
  ([0007](docs/decisions/0007-mongodb-without-replica-set.md))
- **The store chain is ordered by `EventStoreLayer.Order`**, never by call order.
  ([0008](docs/decisions/0008-store-chain-ordered-by-layer.md))
- **Snapshots and the Redis cache are independent modules**; they share only
  `FEB.EventSourcing.StateContracts`.
  ([0002](docs/decisions/0002-separate-snapshots-and-state-cache.md))
- **The snapshot generator is fail-closed**: every member is mapped, marked
  `[IgnoreSnapshot]`, or a compile error. New diagnostics get an entry in
  `docs/generator-reference.md` and a `GeneratorDriver` test.
  ([0004](docs/decisions/0004-fail-closed-snapshot-generator.md))
- **The generator targets netstandard2.0 and Roslyn 4.8**, so it loads in every IDE
  and build host, and it **ships inside `FEB.EventSourcing.StateContracts`**.
  ([0004](docs/decisions/0004-fail-closed-snapshot-generator.md),
  [0014](docs/decisions/0014-generator-ships-in-statecontracts.md))
- **The outbox is at-least-once and tracked per subscriber.** Subscriber names are
  persistence keys: stable, without `.`, `$` or whitespace.
  ([0009](docs/decisions/0009-outbox-named-subscribers.md))
- **`Version` stays `int`.** ([0011](docs/decisions/0011-version-stays-int.md))
- **No AppDomain scanning** for registrations; explicit assemblies and modules only.
  ([0005](docs/decisions/0005-explicit-registration-no-appdomain-scans.md))

## License

By contributing you agree that your contributions are licensed under the
[MIT License](LICENSE) of this project.
