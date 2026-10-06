# FEB.EventSourcing

Core of the FEB.EventSourcing framework: the `AddEventSourcing` builder, the event
store decorator chain, replay and optimistic concurrency, projection updates, sync
event dispatch and the outbox worker with named, independently tracked subscribers
(`IOutboxSubscriber`) for brokers, APIs and in-process async handlers.

Quick start:

```csharp
services.AddEventSourcing(es =>
{
    es.UseMongoDb(connectionString, o => o.EnableSnapshots());
    es.UseSnapshots();
    es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
}, typeof(Order).Assembly);
```

Documentation: see the `docs/` folder in the repository.
