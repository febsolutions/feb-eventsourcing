using FEB.EventSourcing.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FEB.EventSourcing.Redis;

public class RedisRegistration(RedisEventStoreOptions options) : IEventStoreRegistration
{
    public int Order => EventStoreLayer.Cache;

    public IEventStore<TAggregate, TId> CreateEventStore<TAggregate, TId>(IEventStore<TAggregate, TId>? innerStore, IServiceProvider sp, EventStoreOptions eventStoreOptions)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        ArgumentNullException.ThrowIfNull(innerStore);

        var redis = sp.GetRequiredService<IRedisCacheDatabase>();
        var serializer = sp.GetRequiredService<ISnapshotSerializer>();
        var metrics = sp.GetService<IEventStoreMetrics>() ?? new NoOpEventStoreMetrics();
        var snapshotMetadataRegistry = sp.GetService<ISnapshotMetadataRegistry>();
        var aggregateFactory = sp.GetRequiredService<AggregateFactory<TAggregate, TId>>();
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<RedisCacheStore<TAggregate, TId>>();

        var unitOfWork = sp.GetService<IUnitOfWorkContext>() ?? NoUnitOfWorkContext.Instance;

        return new RedisCacheStore<TAggregate, TId>(innerStore, aggregateFactory, snapshotMetadataRegistry, redis, serializer, options, metrics, unitOfWork, logger);
    }

    public Type GetRegisteredType<TAggregate, TId>()
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
        => typeof(RedisCacheStore<TAggregate, TId>);

    public void RegisterEventStore<TAggregate, TId>(IServiceCollection services,
        Type? innerStoreType, EventStoreOptions eventStoreOptions, bool registerAsOuterStore = false) where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        RegisterEventStore<RedisCacheStore<TAggregate, TId>, TAggregate, TId>(services, innerStoreType, eventStoreOptions);
        if (registerAsOuterStore)
            RegisterEventStore<IEventStore<TAggregate, TId>, TAggregate, TId>(services, innerStoreType, eventStoreOptions);
    }

    private void RegisterEventStore<TService, TAggregate, TId>(IServiceCollection services,
        Type? innerStoreType, EventStoreOptions eventStoreOptions)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
        where TService : class
    {
        services.AddScoped<TService>(sp =>
        {
            var innerStore = innerStoreType != null ? sp.GetService(innerStoreType) as IEventStore<TAggregate, TId> : null;
            return (TService)CreateEventStore(innerStore, sp, eventStoreOptions)!;
        });
    }
}
