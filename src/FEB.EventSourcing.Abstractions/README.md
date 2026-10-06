# FEB.EventSourcing.Abstractions

Contracts for the FEB.EventSourcing framework: `AggregateRoot<TAggregate, TId>`,
`IDomainEvent<T>`, `IEventStore<TAggregate, TId>`, persistence and outbox
interfaces, projection writer and event handler contracts, and the metadata types
stamped onto every event.

It also holds `[EventName("order.placed")]`, which gives an event a stable storage
name so the class can later move between namespaces or projects without breaking
stored data (optional — without it the CLR type name is stored).

Reference this package from your domain assemblies; add `FEB.EventSourcing` plus a
persistence package (`FEB.EventSourcing.MongoDb` or `FEB.EventSourcing.InMemory`)
in the composition root.

Documentation: see the `docs/` folder in the repository.
