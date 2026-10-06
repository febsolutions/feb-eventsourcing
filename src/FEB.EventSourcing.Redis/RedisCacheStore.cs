using System.Diagnostics;
using FEB.EventSourcing.Snapshots;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FEB.EventSourcing.Redis;

/// <summary>
/// Version-guarded state cache in front of the event store (cache-aside + write-through).
/// Correctness is still anchored in the persistence layer's optimistic concurrency: an
/// aggregate loaded from a stale cache entry fails cleanly on save and the entry is
/// invalidated. Cache failures never degrade the command path.
/// </summary>
public class RedisCacheStore<TAggregate, TId>(
    IEventStore<TAggregate, TId> innerStore,
    AggregateFactory<TAggregate, TId> aggregateFactory,
    ISnapshotMetadataRegistry? snapshotMetadataRegistry,
    IRedisCacheDatabase redis,
    ISnapshotSerializer serializer,
    RedisEventStoreOptions options,
    IEventStoreMetrics metrics,
    ILogger<RedisCacheStore<TAggregate, TId>>? logger = null)
    : ProxyStore<TAggregate, TId>(innerStore)
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
{
    private const string Source = "redis";

    private readonly ISnapshotMetadata? _meta = snapshotMetadataRegistry?.GetForAggregate(typeof(TAggregate));
    private readonly ILogger _logger = logger ?? NullLogger<RedisCacheStore<TAggregate, TId>>.Instance;

    private static string Key(TId id) => $"{typeof(TAggregate).Name}:{id}";

    public override async Task<TAggregate?> LoadByIdAsync(TId id, CancellationToken cancellationToken = default)
    {
        if (_meta == null)
            return await base.LoadByIdAsync(id, cancellationToken);

        var fromCache = await TryLoadFromCacheAsync(id);
        if (fromCache != null)
            return fromCache;

        var aggregate = await base.LoadByIdAsync(id, cancellationToken);

        if (aggregate != null)
            await TryCacheAsync(aggregate);

        return aggregate;
    }

    public override async Task SaveAsync(TAggregate aggregate, CommandContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await base.SaveAsync(aggregate, context, cancellationToken);
        }
        catch (ConcurrencyException<TId>)
        {
            // The writer had a stale state - a possibly stale cache entry
            // must not be served any further.
            await TryInvalidateAsync(aggregate.Id);
            throw;
        }

        if (_meta != null)
            await TryCacheAsync(aggregate);
    }

    public override async Task<bool> IsExistsAsync(TId id, CancellationToken cancellationToken = default)
    {
        if (_meta == null)
            return await base.IsExistsAsync(id, cancellationToken);

        try
        {
            if (await redis.ExistsAsync(Key(id)))
                return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis exists-check for {Key} failed, falling back to inner store", Key(id));
        }

        return await base.IsExistsAsync(id, cancellationToken);
    }

    private async Task<TAggregate?> TryLoadFromCacheAsync(TId id)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var cached = await redis.GetAsync(Key(id));

            if (cached == null || cached.SchemaVersion != _meta!.Version)
                return null;

            var snapshot = serializer.Deserialize(cached.Payload, _meta.SnapshotType, cached.Compression);

            var aggregate = aggregateFactory.Create(id);
            _meta.RestoreSnapshot(aggregate, snapshot);
            aggregate.RestoreState(cached.StreamVersion, []);

            sw.Stop();
            metrics.IncrementRead<TAggregate>(Source);
            metrics.RecordRead<TAggregate>(Source, sw.Elapsed.TotalSeconds);

            return aggregate;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis cache read for {Key} failed, falling back to inner store", Key(id));
            await TryInvalidateAsync(id);
            return null;
        }
    }

    private async Task TryCacheAsync(TAggregate aggregate)
    {
        try
        {
            var snapshot = _meta!.CreateSnapshot(aggregate);
            var payload = serializer.Serialize(snapshot, _meta.SnapshotType, options.Compression);

            var state = new CachedAggregateState(
                payload,
                aggregate.Version,
                _meta.Version,
                options.Compression,
                DateTime.UtcNow);

            await redis.TrySetAsync(Key(aggregate.Id), state, JitteredTtl());

            metrics.IncrementWrite<TAggregate>(Source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis cache write for {Key} failed", Key(aggregate.Id));
        }
    }

    private async Task TryInvalidateAsync(TId id)
    {
        try
        {
            await redis.DeleteAsync(Key(id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis cache invalidation for {Key} failed", Key(id));
        }
    }

    private TimeSpan JitteredTtl()
    {
        if (options.TtlJitter <= 0)
            return options.Ttl;

        var factor = 1 + (Random.Shared.NextDouble() * 2 - 1) * options.TtlJitter;
        return TimeSpan.FromTicks((long)(options.Ttl.Ticks * factor));
    }
}
