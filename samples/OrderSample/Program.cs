using FEB.EventSourcing;
using FEB.EventSourcing.InMemory;
using FEB.EventSourcing.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using OrderSample;

// 1) Wire up the event sourcing framework.
//    InMemory keeps the sample runnable without any infrastructure — swap the
//    persistence line for MongoDB (and optionally add Redis) in a real app:
//
//        es.UseMongoDb("mongodb://localhost:27017/orders", o => o.EnableSnapshots());
//        // optional, requires a replica set (single-node is enough): atomic appends
//        es.UseMongoDb("mongodb://localhost:27017/orders", o => o.UseTransactions());
//     or es.UsePostgres("Host=localhost;Database=orders;Username=app;Password=…", o => o.EnableSnapshots());
//     or es.UseSqlServer("Server=localhost;Database=orders;…;TrustServerCertificate=true", o => o.EnableSnapshots());
//        es.UseRedis("localhost:6379", o => o.SetCachePrefix("orders"));
//
//    Outbox (needs MongoDB): every saved event is delivered at-least-once to each
//    registered subscriber, tracked independently — see docs/outbox.md:
//
//        es.UseMongoOutbox(o => o.SetMaxAttempts(10));   // or es.UseSqlOutbox(...)
//        es.UseDefaultOutboxSubscriber();                 // "handlers": IASyncEventHandler<T>
//        es.UseOutboxSubscriber<ConsoleBrokerPublisher>(); // "console-broker": your broker/API
//        es.UseOutboxWorker(o => o.SetPollingInterval(TimeSpan.FromSeconds(1)));
//
var services = new ServiceCollection();
services.AddSingleton<OrderOverview>();

services.AddEventSourcing(es =>
    {
        es.UseInMemory();
        es.UseSnapshots();

        es.Aggregate<Order, string>(a =>
        {
            a.Factory(Order.CreateNew);
            a.Snapshots(s => s.EveryNEvents(10));
        });
    },
    typeof(Order).Assembly); // scanned for projections and event handlers

await using var provider = services.BuildServiceProvider();

var store = provider.GetRequiredService<IEventStore<Order, string>>();

// 2) Command #1: place an order with two lines. One command = one save = one event batch.
var orderId = $"order-{Guid.NewGuid():N}"[..14];

var order = Order.CreateNew(orderId);
order.Place("ACME Corp.");
order.AddLine("Widget", 3, 19.90m);
order.AddLine("Gadget", 1, 149.00m);

await store.SaveAsync(order, NewCommandContext());
Console.WriteLine($"1) saved {orderId}: version {order.Version}, total {order.Total:0.00}");

// 3) Command #2: load a fresh copy (replayed from events), then ship it.
var loaded = await store.LoadByIdAsync(orderId)
             ?? throw new InvalidOperationException("order not found");

loaded.Ship();
await store.SaveAsync(loaded, NewCommandContext());
Console.WriteLine($"2) shipped {orderId}: version {loaded.Version}, status {loaded.Status}");

// 4) The event stream is the source of truth:
var events = await store.LoadEventsAsync(orderId);
Console.WriteLine($"3) event stream ({events.Count} events):");
foreach (var envelope in events)
    Console.WriteLine($"   v{envelope.Metadata.Version}: {envelope.Payload.GetType().Name}");

// 5) The projection was updated on every save:
var overview = provider.GetRequiredService<OrderOverview>();
Console.WriteLine($"4) read model: {overview.Rows[orderId]}");

// 6) Snapshots: the generator produced a typed DTO + Create/Restore methods.
var snapshot = loaded.CreateSnapshot();
var restored = Order.CreateNew(orderId);
restored.RestoreFromSnapshot(snapshot);
Console.WriteLine($"5) snapshot roundtrip: {restored.Customer}, {restored.Lines.Count} lines, total {restored.Total:0.00}");

return;

// Every command carries tracing/tenant metadata that ends up on each event.
static CommandContext NewCommandContext() => new()
{
    CommandId = Guid.NewGuid(),
    CorrelationId = Guid.NewGuid().ToString("N"),
    UserId = "sample-user",
    ReceivedAt = DateTime.UtcNow
};
