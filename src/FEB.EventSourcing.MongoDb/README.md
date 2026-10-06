# FEB.EventSourcing.MongoDb

MongoDB persistence for the FEB.EventSourcing framework: event streams with atomic
version compare-and-set, snapshot storage (BSON, optional LZ4/Zstd compression) and
a lease-based outbox with per-subscriber delivery tracking and dead-lettering. Runs on
standalone MongoDB (no replica set required); with `UseTransactions()` on a replica set,
events and outbox envelopes are written in one transaction.

```csharp
es.UseMongoDb("mongodb://localhost:27017/mydb", o => o.EnableSnapshots());
es.UseMongoOutbox();
```

Documentation: see the `docs/` folder in the repository.
