using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

internal sealed class EventSourcingBuilder : IEventSourcingBuilder
{
    private readonly List<(Type AggregateType, Type IdType)> _registeredAggregates = [];

    public IServiceCollection Services { get; }

    public IEventStoreChainBuilder EventStoreChainBuilder { get; } = new EventStoreChainBuilder();

    public IReadOnlyList<System.Reflection.Assembly> ApplicationAssemblies { get; }

    public IReadOnlyList<(Type AggregateType, Type IdType)> RegisteredAggregates => _registeredAggregates;

    public EventSourcingBuilder(IServiceCollection services, IReadOnlyList<System.Reflection.Assembly> applicationAssemblies)
    {
        Services = services;
        ApplicationAssemblies = applicationAssemblies;
    }

    internal void AddRegisteredAggregate(Type aggregateType, Type idType)
        => _registeredAggregates.Add((aggregateType, idType));

    public void RegisterInfrastructure(
        Action<IServiceCollection> register)
    {
        register(Services);
    }

    public void SetPersistenceRegistered()
    {
        if (IsPersistenceRegistered)
            throw new InvalidOperationException("Persistence already registered.");
        IsPersistenceRegistered = true;
    }

    public bool IsPersistenceRegistered { get; private set; }
}