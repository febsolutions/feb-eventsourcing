using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

public sealed class AggregateBuilder<TAggregate, TId>
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
{
    private readonly IServiceCollection _services;

    private AggregateFactory<TAggregate, TId>? _factory;

    private SnapshotOptions? _snapshotOptions;

    internal AggregateBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public AggregateBuilder<TAggregate, TId> Factory(Func<TId, TAggregate> creator)
    {
        _factory = new AggregateFactory<TAggregate, TId>(creator);
        return this;
    }

    public AggregateBuilder<TAggregate, TId> Snapshots(
        Action<SnapshotBuilder> configure)
    {
        var builder = new SnapshotBuilder();
        configure(builder);
        _snapshotOptions = builder.Build();
        return this;
    }

    internal void Build(IEventStoreChainBuilder chainBuilder)
    {
        if (_factory == null)
            throw new InvalidOperationException(
                $"Aggregate {typeof(TAggregate).Name} requires a factory.");

        RegisterAggregateFactory();
        RegisterEventStore(chainBuilder);
    }

    private void RegisterEventStore(IEventStoreChainBuilder chainBuilder)
    {
        var options = new EventStoreOptions();
        if (_snapshotOptions != null)
        {
            options.SetSnapshotsEveryNEvents(_snapshotOptions.EveryNEvents);
        }
        
        chainBuilder.Build<TAggregate, TId>(_services, options);
    }

    private void RegisterAggregateFactory()
    {
        if (_factory == null) 
            throw new Exception("Aggregate factory not set.");
        _services.AddSingleton(_factory);
    }
}