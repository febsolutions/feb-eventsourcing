# FEB.EventSourcing.Postgres

PostgreSQL persistence for the FEB.EventSourcing framework (Npgsql): transactional
event append with optimistic concurrency, snapshots (JSON, optional LZ4/Zstd) and
the outbox with per-subscriber delivery tracking (`SKIP LOCKED` leasing).

```csharp
es.UsePostgres("Host=localhost;Database=orders;Username=app;Password=…", o => o.EnableSnapshots());
es.UseSqlOutbox();
```

Documentation: see the `docs/` folder in the repository (`sql.md`).
