using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Snapshots;

/// <summary>Registers the provider-neutral <see cref="SnapshotStore{TAggregate,TId}"/> decorator.</summary>
public sealed class SnapshotStoreRegistration : IEventStoreRegistration
{
    public int Order => EventStoreLayer.Snapshots;

    public IEventStore<TAggregate, TId> CreateEventStore<TAggregate, TId>(IEventStore<TAggregate, TId>? innerStore,
        IServiceProvider sp, EventStoreOptions options)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        ArgumentNullException.ThrowIfNull(innerStore);

        var persistence = sp.GetRequiredService<ISnapshotPersistence>();
        var registry = sp.GetService<ISnapshotMetadataRegistry>();
        var factory = sp.GetRequiredService<AggregateFactory<TAggregate, TId>>();
        var queue = sp.GetService<ISnapshotWriteQueue>();

        return new SnapshotStore<TAggregate, TId>(persistence, innerStore, factory, registry, options, queue);
    }

    public Type GetRegisteredType<TAggregate, TId>()
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
        => typeof(SnapshotStore<TAggregate, TId>);

    public void RegisterEventStore<TAggregate, TId>(IServiceCollection services,
        Type? innerStoreType, EventStoreOptions eventStoreOptions, bool registerAsOuterStore = false)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        services.AddScoped(sp =>
        {
            var inner = innerStoreType != null ? sp.GetService(innerStoreType) as IEventStore<TAggregate, TId> : null;
            return (SnapshotStore<TAggregate, TId>)CreateEventStore(inner, sp, eventStoreOptions);
        });

        if (registerAsOuterStore)
            services.AddScoped<IEventStore<TAggregate, TId>>(sp => sp.GetRequiredService<SnapshotStore<TAggregate, TId>>());
    }
}
