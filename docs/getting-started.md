# Getting started

This chapter takes you from an empty project to a running event-sourced aggregate
with a projection. It deliberately explains *why* each piece exists; if you just
want the API surface, jump to the [concepts](concepts.md) chapter and the reference
sections.

## What you get

FEB.EventSourcing is a small, opinionated framework for **event-sourced aggregates**
in .NET:

- Aggregates change state only by raising events; the event stream is the source of
  truth and is persisted append-only (MongoDB, PostgreSQL or SQL Server in
  production, in-memory for tests).
- Optimistic concurrency guarantees that two writers never silently overwrite each
  other.
- Read models (projections) are updated in the same call that saves the events.
- Side effects that must not block or fail the command (mails, brokers, external
  APIs) go through an outbox with independently tracked subscribers, written in the
  same transaction as the events on PostgreSQL, SQL Server and MongoDB replica sets.
- Snapshots (generated at compile time) and an optional Redis state cache keep loads
  fast as streams grow.

It is *not* a full CQRS/DDD framework: there are no sagas, no process managers, no
command bus of its own (see [FEB.Cqrs](cqrs.md) for the tiny dispatcher we ship).

## Packages

| Package | You need it when… |
|---|---|
| `FEB.EventSourcing.Abstractions` | always — contracts for domain assemblies (`AggregateRoot`, `IDomainEvent<T>`, `IEventStore<,>`) |
| `FEB.EventSourcing` | always — the builder (`AddEventSourcing`), store chain, projections, outbox worker |
| `FEB.EventSourcing.MongoDb` | production persistence on MongoDB (events, snapshots, outbox) |
| `FEB.EventSourcing.Postgres` / `FEB.EventSourcing.SqlServer` | production persistence on PostgreSQL / SQL Server (same features; both build on `FEB.EventSourcing.Sql`) |
| `FEB.EventSourcing.InMemory` | tests and local development without infrastructure |
| `FEB.EventSourcing.StateContracts` | you want `[AutoSnapshot]` (attributes + snapshot contracts + the source generator) |
| `FEB.EventSourcing.Snapshots` | you want snapshots wired into the store (`UseSnapshots`, background writes) |
| `FEB.EventSourcing.Redis` | hot aggregates should be served from a Redis state cache |
| `FEB.EventSourcing.Metrics` | Prometheus counters/histograms for reads and writes |
| `FEB.EventSourcing.TestKit` | your test project — proves that snapshots do not lose state |
| `FEB.Cqrs` | a minimal command/query dispatcher, independent of the rest |

Reference `Abstractions` from your domain project and everything else from the
composition root (web host, worker, tests).

## Step 1 — the aggregate

An aggregate is a class deriving from `AggregateRoot<TAggregate, TId>`. It holds
state as ordinary properties, exposes *command methods* that validate invariants,
and changes state **only** by raising events. The example (from
`samples/OrderSample`) is a small order:

```csharp
[AutoSnapshot] // optional; see the snapshots chapter
public partial class Order : AggregateRoot<Order, string>
{
    public string Customer { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
    public decimal Total { get; set; }

    public override void EnsureHasId()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("Id must be set!");
    }

    public void Place(string customer)
    {
        if (Status != OrderStatus.None)
            throw new InvalidOperationException("Order was already placed.");

        Raise(new OrderPlaced(customer));
    }

    public void AddLine(string article, int quantity, decimal unitPrice)
    {
        if (Status != OrderStatus.Placed)
            throw new InvalidOperationException("Lines can only be added to a placed order.");

        Raise(new OrderLineAdded(article, quantity, unitPrice));
    }
}
```

Points worth understanding:

- **`Raise(event)`** does two things: it applies the event to `this` (so the
  aggregate's state is immediately correct within the command) and records the event
  as *uncommitted*. `SaveAsync` later persists exactly those uncommitted events.
- **Command methods validate, events don't.** `Place` checks that the order was not
  placed before; the event `OrderPlaced` merely records that it happened. When the
  stream is replayed, events are applied without re-running validation — the
  validation already happened when the event was originally raised.
- **`EnsureHasId`** is your guard for the id type. The framework calls it on
  creation and replay.
- **State properties need setters** so events can apply themselves. If you prefer
  private setters, that works too (`ApplyTo` runs inside the aggregate's type when
  the event is a nested type; otherwise use `internal`).
- `Id` and `Version` come from the base class. `Version` is the index of the last
  applied event (0-based); a fresh aggregate is at `-1`.

## Step 2 — the events

Events are immutable records implementing `IDomainEvent<TAggregate>`. Each knows
how to apply itself:

```csharp
public sealed record OrderPlaced(string Customer) : IDomainEvent<Order>
{
    public void ApplyTo(Order obj)
    {
        obj.Customer = Customer;
        obj.Status = OrderStatus.Placed;
    }
}

public sealed record OrderLineAdded(string Article, int Quantity, decimal UnitPrice) : IDomainEvent<Order>
{
    public void ApplyTo(Order obj)
    {
        obj.Lines.Add(new OrderLine { Article = Article, Quantity = Quantity, UnitPrice = UnitPrice });
        obj.Total += Quantity * UnitPrice;
    }
}
```

Guidelines for events (they are your long-term contract — see
[concepts → event design](concepts.md#event-design-and-versioning)):

- Name them in past tense, from the domain's point of view (`OrderPlaced`, not
  `SetStatus`).
- Keep them small and self-contained; include the data needed to apply them, not
  the whole aggregate.
- Never throw inside `ApplyTo`. It runs during replay of history you cannot change.
- Only additive changes later on (new optional properties). Removing or renaming a
  property means old stored events can no longer be read — introduce a new event
  type instead.

## Step 3 — wire it up

In the composition root:

```csharp
services.AddEventSourcing(es =>
    {
        // persistence first — everything else builds on it (pick one)
        es.UseMongoDb("mongodb://localhost:27017/orders", o => o.EnableSnapshots());
        // es.UsePostgres("Host=localhost;Database=orders;…", o => o.EnableSnapshots());
        // es.UseSqlServer("Server=localhost;Database=orders;…", o => o.EnableSnapshots());

        // optional layers
        es.UseSnapshots();
        // es.UseRedis("localhost:6379", o => o.SetCachePrefix("orders"));
        // es.UseMongoOutbox(); /* or es.UseSqlOutbox(); */ es.UseOutboxWorker();
        // es.UsePrometheusMetrics();

        // one registration per aggregate type
        es.Aggregate<Order, string>(a =>
        {
            a.Factory(Order.CreateNew);              // how to create an empty instance for replay
            a.Snapshots(s => s.EveryNEvents(50));    // cadence for this type (0 = off)
        });
    },
    typeof(Order).Assembly);  // assemblies scanned for handlers, projections, event types
```

What each part means:

- **`AddEventSourcing(configure, assemblies)`** — the entry point. The assemblies
  are scanned for `IAggregateProjectionWriter<T>`, `IEventProjectionWriter<T,TId>`,
  `ISyncEventHandler<TEvent>` and `IASyncEventHandler<TEvent>` implementations
  (registered as scoped services) and for `IEvent` types (BSON class maps). List
  every assembly that contains any of these.
- **`UseMongoDb(...)` / `UsePostgres(...)` / `UseSqlServer(...)`** — exactly one
  persistence, and it *must* be registered before other layers; the builder enforces
  it. `UseInMemory()` is the alternative for tests.
- **`Aggregate<T, TId>(...)`** — registers `IEventStore<T, TId>` in DI. The factory
  tells the store how to create an empty instance for replay (`CreateNew` from the
  base class is usually right).
- The order of `Use*()` calls does **not** matter for the store chain — layers sort
  themselves (see [concepts → store chain](concepts.md#the-store-chain-decorators)).

## Step 4 — save and load

Inject `IEventStore<Order, string>` wherever you handle commands:

```csharp
public sealed class PlaceOrderHandler(IEventStore<Order, string> store)
{
    public async Task HandleAsync(PlaceOrder cmd, CancellationToken ct)
    {
        var order = Order.CreateNew(cmd.OrderId);
        order.Place(cmd.Customer);
        foreach (var line in cmd.Lines)
            order.AddLine(line.Article, line.Quantity, line.UnitPrice);

        await store.SaveAsync(order, new CommandContext
        {
            CommandId = cmd.CommandId,          // becomes CausationId on every event
            CorrelationId = cmd.CorrelationId,  // flows through handlers and the outbox
            TenantId = cmd.TenantId,
            UserId = cmd.UserId,
            ReceivedAt = DateTime.UtcNow
        }, ct);
    }
}
```

And to change an existing order:

```csharp
var order = await store.LoadByIdAsync(orderId, ct)
            ?? throw new NotFoundException(orderId);

order.Ship();
await store.SaveAsync(order, context, ct);
```

What happens inside `SaveAsync`, in order:

1. If there are no uncommitted events, return immediately (no-op).
2. Wrap each uncommitted event in an envelope with metadata from the
   `CommandContext` (event id, aggregate id/type, version, timestamps, tenant, user,
   correlation, causation).
3. Append the batch with the aggregate's current version as *expected version*. If
   another writer appended first → `ConcurrencyException<TId>`, nothing is written.
4. Commit the aggregate: uncommitted events cleared, `Version` advanced.
5. Run projection writers (`IAggregateProjectionWriter<T>`, `IEventProjectionWriter<T,TId>`).
6. Enqueue the events into the outbox (if configured).
7. Dispatch to `ISyncEventHandler<TEvent>` implementations (in-process, same call).
8. Snapshot / cache layers do their work (if configured).

One command = one `SaveAsync` = one atomically appended batch. Do not call
`SaveAsync` several times within one command unless you want several batches.

## Step 5 — a projection

Read models live outside the aggregate. A projection writer receives the
up-to-date aggregate after every successful save:

```csharp
public sealed class OrderOverviewProjection(OrderOverview overview) : IAggregateProjectionWriter<Order>
{
    public Task UpdateAsync(Order aggregate, ProjectionContext context, CancellationToken cancellationToken)
    {
        overview.Rows[aggregate.Id] = $"{aggregate.Customer} {aggregate.Status} {aggregate.Total:0.00}";
        return Task.CompletedTask;
    }
}
```

It is discovered by the assembly scan — no registration needed. In a real
application the projection writes to a MongoDB collection, a SQL table, a search
index — whatever your queries need. Because projections run inside `SaveAsync`,
they must be fast and must not throw for transient reasons (a throwing projection
fails the command after the events were already persisted; see
[concepts → failure handling](concepts.md#failure-handling)).

## Step 6 — run it

```bash
dotnet run --project samples/OrderSample
```

The sample uses `UseInMemory()` so it runs without infrastructure and prints every
step: save, replay-load, the raw event stream, the read model, and a snapshot
roundtrip. Swap in `UseMongoDb(...)`, `UsePostgres(...)` or `UseSqlServer(...)` and
it behaves the same against a database.

## Where to go next

- [Concepts](concepts.md) — everything above in depth: the store chain, versioning,
  concurrency, projections, event design, failure handling.
- [MongoDB](mongodb.md) or [SQL](sql.md) — storage layout, atomicity, operations.
- [Snapshots](snapshots.md) and [Redis cache](redis-cache.md) — performance.
- [Outbox](outbox.md) — reliable delivery to brokers, APIs and async handlers.
- [Testing](testing.md) — InMemory store, TestKit, Testcontainers.
