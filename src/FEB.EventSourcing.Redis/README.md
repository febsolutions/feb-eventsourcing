# FEB.EventSourcing.Redis

Redis aggregate cache for the FEB.EventSourcing framework. Caches the current state
of `[AutoSnapshot]` aggregates as one Redis hash per aggregate, written through a
version-guarded Lua compare-and-set. Cache-aside on miss, write-through on save,
invalidation on concurrency conflicts — Redis failures always degrade gracefully to
the underlying store.

```csharp
es.UseRedis("localhost:6379", o =>
{
    o.SetCachePrefix("myapp");
    o.SetTtl(TimeSpan.FromMinutes(30));
});
```

Documentation: see the `docs/` folder in the repository.
