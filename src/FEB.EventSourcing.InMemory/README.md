# FEB.EventSourcing.InMemory

In-memory persistence for the FEB.EventSourcing framework — a complete,
concurrency-checked event store for unit tests and local development. Behaves like
the production stores, including `ConcurrencyException<TId>` on version conflicts.

```csharp
services.AddEventSourcing(es =>
{
    es.UseInMemory();
    es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
}, typeof(Order).Assembly);
```
