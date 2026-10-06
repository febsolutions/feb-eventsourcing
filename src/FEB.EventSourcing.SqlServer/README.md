# FEB.EventSourcing.SqlServer

SQL Server persistence for the FEB.EventSourcing framework (Microsoft.Data.SqlClient):
transactional event append with optimistic concurrency, snapshots (JSON, optional
LZ4/Zstd) and the outbox with per-subscriber delivery tracking (`READPAST` leasing); outbox envelopes are written in the same transaction
as the events.

```csharp
es.UseSqlServer("Server=localhost;Database=orders;User Id=app;Password=…;TrustServerCertificate=true", o => o.EnableSnapshots());
es.UseSqlOutbox();
```

Documentation: see the `docs/` folder in the repository (`sql.md`).
