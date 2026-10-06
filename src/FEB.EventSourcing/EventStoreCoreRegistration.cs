using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

internal class EventStoreCoreRegistration : IEventStoreRegistration
{
    public int Order => EventStoreLayer.Core;

    public IEventStore<TAggregate, TId> CreateEventStore<TAggregate, TId>(IEventStore<TAggregate, TId>? innerStore,
        IServiceProvider sp, EventStoreOptions options)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        if (innerStore != null)
            throw new InvalidOperationException("Inner Store must be null!");
        
        var eventStorePersistance = sp.GetRequiredService<IEventStorePersistence>();
        var metrics = sp.GetService<IEventStoreMetrics>() ?? new NoOpEventStoreMetrics();

        var aggregateFactory = sp.GetRequiredService<AggregateFactory<TAggregate, TId>>();
        var projectionUpdater = sp.GetRequiredService<IProjectionUpdater>();
        
        var eventDispatcher = sp.GetService<IEventDispatcher>();
        var outbox = sp.GetService<IOutbox>();

        var eventStore = new CoreEventStore<TAggregate, TId>(
            eventStorePersistance,
            aggregateFactory,
            projectionUpdater,
            eventDispatcher,
            outbox,
            metrics);
        
        return eventStore;
    }
    
    public Type GetRegisteredType<TAggregate, TId>()
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new() 
        => typeof(CoreEventStore<TAggregate, TId>);

    public void RegisterEventStore<TAggregate, TId>(IServiceCollection services, 
        Type? innerStoreType, EventStoreOptions eventStoreOptions, bool registerAsOuterStore = false) where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        RegisterEventStore<CoreEventStore<TAggregate, TId>, TAggregate, TId>(services, innerStoreType, eventStoreOptions);
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
            return (TService)CreateEventStore(innerStore, sp, eventStoreOptions);
        });
    }
}