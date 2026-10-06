# Testing

Testing an event-sourced application has three layers, and the framework supports
each: fast **unit tests** of aggregates without any store, **integration tests** of
command handlers and projections against the in-memory store, and **contract
tests** for snapshots plus **infrastructure tests** with real database/Redis
containers.

## 1. Aggregates without a store

Aggregates are plain objects; command methods raise events, `GetUncommittedEvents()`
shows what happened. This is the fastest and most valuable test layer — pure
domain logic, no infrastructure, no DI:

```csharp
[Fact]
public void Placing_an_order_raises_OrderPlaced()
{
    var order = Order.CreateNew("o1");

    order.Place("ACME");

    order.GetUncommittedEvents().Should().ContainSingle()
        .Which.Should().BeOfType<OrderPlaced>()
        .Which.Customer.Should().Be("ACME");
    order.Status.Should().Be(OrderStatus.Placed);
}

[Fact]
public void Cannot_ship_an_unplaced_order()
{
    var order = Order.CreateNew("o1");

    var act = () => order.Ship();

    act.Should().Throw<InvalidOperationException>();
}
```

To test *replay* behaviour, build history explicitly:

```csharp
var order = Order.CreateNew("o1");
order.Replay(new IDomainEvent<Order>[]
{
    new OrderPlaced("ACME"),
    new OrderLineAdded("Widget", 2, 9.90m)
});
order.Version.Should().Be(1);
order.Total.Should().Be(19.80m);
```

## 2. Command handlers and projections with the InMemory store

`FEB.EventSourcing.InMemory` is a complete persistence: it enforces optimistic
concurrency (`ConcurrencyException<TId>`), runs projections and sync handlers,
and needs nothing but DI. Build the same `AddEventSourcing` configuration you use
in production, swapping the persistence line:

```csharp
public static ServiceProvider BuildTestHost()
{
    var services = new ServiceCollection();
    services.AddSingleton<OrderOverview>();          // your in-memory read model

    services.AddEventSourcing(es =>
    {
        es.UseInMemory();
        es.UseSnapshots(SnapshotMetadataModule_MyDomain.CreateAll());   // optional
        es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
    }, typeof(Order).Assembly);

    return services.BuildServiceProvider();
}

[Fact]
public async Task PlaceOrder_updates_the_overview()
{
    await using var sp = BuildTestHost();
    var handler = ActivatorUtilities.CreateInstance<PlaceOrderHandler>(sp);

    await handler.HandleAsync(new PlaceOrder("o1", "ACME", ...), CancellationToken.None);

    sp.GetRequiredService<OrderOverview>().Rows.Should().ContainKey("o1");
}
```

Things to know:

- The in-memory store is a **singleton per service provider** — one provider per
  test (or per test class with unique ids) keeps tests isolated.
- Concurrency behaves like production: load two copies, save both → the second
  throws `ConcurrencyException<TId>`. Test your retry logic here.
- The outbox needs a real persistence (there is no in-memory outbox); async handlers
  therefore do not fire in this setup. Test them either directly (call `HandleAsync`)
  or in the integration tests below.
- The InMemory store does not snapshot or cache — those are persistence/Redis layers.

## 3. Snapshot contract assertions (TestKit)

Add `FEB.EventSourcing.TestKit` to the test project and one test per application:

```csharp
[Fact]
public void All_aggregates_roundtrip_completely()
    => SnapshotContract.AssertRoundtripsAll(typeof(Order).Assembly);
```

For every generated snapshot metadata in the assembly it:

1. creates a fresh aggregate and fills every writable property with deterministic
   random data — recursively, including base-class properties and lists;
2. calls `CreateSnapshot()`, serializes/deserializes the DTO with System.Text.Json
   (exactly what the Redis cache does), calls `RestoreFromSnapshot()` on a fresh
   instance and `CreateSnapshot()` again;
3. deep-compares the two DTOs and fails with the exact property paths that differ,
   e.g. `Order.Lines[1].UnitPrice: expected '12.34', actual '0'`;
4. lists all `[IgnoreSnapshot]` members in the failure output, so intentionally
   excluded state is visible when something is off.

Why this test matters even though the generator is fail-closed: the generator
guarantees no member is *forgotten*; this test guarantees the round trip is
*correct* — including hand-written `OnAfterRehydrate` logic and members that were
excluded but are actually state.

Overloads: `AssertRoundtrip(meta)` for a single aggregate,
`AssertRoundtripsAll(assembly, filter, seed)` to exclude types or change the data,
`DescribeIgnoredMembers(type)` for review output.

## 4. Integration tests with real infrastructure (Testcontainers)

For persistence-specific behaviour (atomicity, indexes/schema, outbox, snapshots) and
Redis (CAS, TTL) the framework's own suite uses Testcontainers; copy the fixtures:

```csharp
public sealed class MongoDbFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container =
        new MongoDbBuilder().WithImage("mongo:8").WithReplicaSet().Build();

    public string GetConnectionString(string database)
        => new MongoUrlBuilder(_container.GetConnectionString())
            { DatabaseName = database, AuthenticationSource = "admin", DirectConnection = true }
            .ToMongoUrl().ToString();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("mongo")]
public class MongoCollection : ICollectionFixture<MongoDbFixture>;
```

Same idea for Redis (`RedisBuilder`), PostgreSQL (`PostgreSqlBuilder`) and SQL Server
(`MsSqlBuilder`). Practical tips from the framework's suite:

- Share one container per test collection; isolate tests by **unique database
  names** (Mongo) / **unique schemas** (SQL) / **unique key prefixes** (Redis) /
  unique aggregate ids.
- Hosted services (`OutboxWorker`, `MongoDbIndexInitializer`, `SqlSchemaInitializer`,
  `SnapshotWriteWorker`)
  do not start in a bare `ServiceProvider`. Resolve them via
  `sp.GetServices<IHostedService>()` and call `StartAsync`, or use their direct
  entry points (`OutboxWorker.ProcessBatchAsync` is internal — visible to the
  framework tests; in your tests start the service or use `WebApplicationFactory`).
- `MongoDbIndexInitializer.EnsureAllAsync()` / `SqlSchemaInitializer.EnsureAsync()` are
  public — call them if a test relies on indexes/tables existing.
- The fixtures support two modes: by default Testcontainers starts the databases
  (Docker required — CI does exactly this on GitHub-hosted runners); alternatively set
  `ES_TEST_MONGO` / `ES_TEST_REDIS` / `ES_TEST_POSTGRES` / `ES_TEST_SQLSERVER` to
  point at servers you already run.
- MongoDB runs as a **single-node replica set** in both modes (locally via
  `WithReplicaSet()`, in CI the service starts with `--replSet rs0` and the fixture
  runs `replSetInitiate` idempotently), so the transactional append mode is testable;
  all connections use `directConnection=true`.

## 5. Testing outbox subscribers

A subscriber is a plain class — unit test it by calling `DispatchAsync` with a
hand-built `OutboxEnvelope`. For end-to-end (save → outbox → subscriber), use the
Mongo or SQL fixture, register your subscriber via `UseOutboxSubscriber<T>()`, save an
aggregate and drive the worker; see `OutboxTests` in the framework suite for the
pattern including the multi-subscriber partial-failure case.

## 6. Test coverage

The framework suite (`src/FEB.EventSourcing.Tests`, 179 tests) is measured with
Coverlet and gated at **85 % line coverage**: locally via `scripts/coverage.sh`,
in CI in the test step of the GitHub Actions workflow (same Coverlet threshold).

Unit and integration tests build against project references, so they cannot see
packaging defects. `scripts/package-smoke-test.sh` closes that gap: it packs every
package into a local feed and builds and runs `tests/PackageConsumer`, an application
that references only `FEB.EventSourcing.Postgres` and uses `[AutoSnapshot]` — it fails
unless the snapshot source generator reaches the application through the package
dependencies. CI runs it on every change, and the release workflow publishes only
packages that passed it.

Current coverage (2026-10-06, `./scripts/coverage.sh`):

| Package | Line | Branch | Method |
|---|---|---|---|
| FEB.EventSourcing.StateContracts | 100 % | 100 % | 100 % |
| FEB.EventSourcing.SqlServer | 99.1 % | – | 90.9 % |
| FEB.EventSourcing.Postgres | 99.0 % | – | 90.9 % |
| FEB.EventSourcing.InMemory | 94.7 % | 85.0 % | 100 % |
| FEB.EventSourcing.MongoDb | 91.2 % | 80.2 % | 93.0 % |
| FEB.EventSourcing.Abstractions | 91.0 % | 89.3 % | 85.8 % |
| FEB.EventSourcing.Snapshots | 87.9 % | 75.0 % | 94.7 % |
| FEB.EventSourcing.Snapshots.Generator | 87.7 % | 75.0 % | 100 % |
| FEB.EventSourcing | 86.1 % | 79.0 % | 92.0 % |
| FEB.EventSourcing.Sql | 85.7 % | 72.3 % | 85.1 % |
| FEB.EventSourcing.Redis | 84.5 % | 65.5 % | 90.9 % |
| FEB.EventSourcing.TestKit | 78.8 % | 75.8 % | 85.7 % |
| **Total** | **88.4 %** | **76.1 %** | **90.6 %** |

`FEB.EventSourcing.Metrics` and `FEB.Cqrs` are outside the gate's include filter
(`[FEB.EventSourcing*]*`); `FEB.Cqrs` has its own dispatcher tests. What the suite
covers, by area:

| Area | Tests |
|---|---|
| Aggregate semantics (raise/replay/commit/restore) | unit |
| InMemory store: roundtrip, concurrency, ids, metadata, rebuild | unit |
| Handler/projection scan registration, sync dispatch | unit |
| Generator: every diagnostic (positive + negative), inheritance, shared nested types, recursive hash, `List<primitive>`, module emission, aggregates in the global namespace | `GeneratorDriver` |
| Packaging: generator ships in the packages and reaches a consuming application transitively | package smoke test (`tests/PackageConsumer`) |
| Snapshot roundtrips (generated code), registry, metadata module, serializers (BSON/JSON × None/LZ4/Zstd) | unit |
| Snapshot cadence crossing check, background write worker | unit |
| Redis store: cache-aside, hit, write-through, invalidation on conflict, schema mismatch, corrupt payload, passthrough | unit (fake DB) |
| Redis database: CAS semantics, TTL expiry, delete, full-chain hit, stale-writer recovery | Testcontainers |
| **Provider contract** (runs once each for MongoDB, PostgreSQL, SQL Server): roundtrip, unknown id, metadata incl. headers/UTC, multi-save + fromVersion, exists/ids, stale writer, creation race, snapshot + delta + save after snapshot load, outbox multi-subscriber partial failure | Testcontainers |
| MongoDB specifics: BSON metadata, background snapshots, unique index creation, idempotent init, version self-heal (both directions, missing doc), duplicate-insert reconciliation (own retry completes, foreign orphan throws) | Testcontainers |
| MongoDB transactions (`UseTransactions`): roundtrip, stale writer incl. `ActualVersion`, atomic abort against orphan, creation race | Testcontainers (single-node RS) |
| Outbox: default delivery, completion, per-subscriber dead-letter, one-failing-of-many, legacy envelopes, dispatcher adapter | Testcontainers |
| Stable event names (`[EventName]`): attribute reading, name/alias resolution, CLR fallback, duplicate rejection, actionable error | unit |
| Event names per provider: stored discriminator / `event_type` value, legacy CLR names and aliases still readable, outbox payload + dispatcher resolution | Testcontainers (Mongo, Postgres, SQL Server) |
| Chain order: layer sorting, custom layer | unit + Testcontainers |
| TestKit contract assertions | unit |
| Cqrs dispatchers | unit |

When you change framework code, keep this table and the numbers current (re-run
`./scripts/coverage.sh`); application projects are free to set their own bar — the
InMemory store makes high coverage of command handlers cheap.
