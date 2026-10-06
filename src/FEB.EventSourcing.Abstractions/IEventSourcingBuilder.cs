using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

public interface IEventSourcingBuilder
{
    IServiceCollection Services { get; }

    IEventStoreChainBuilder EventStoreChainBuilder { get; }

    /// <summary>The application assemblies passed to <c>AddEventSourcing</c> — scanned for handlers, projections and event types.</summary>
    IReadOnlyList<System.Reflection.Assembly> ApplicationAssemblies { get; }

    /// <summary>Aggregate/id type pairs registered via <c>es.Aggregate&lt;T, TId&gt;()</c> — for startup tasks such as index creation.</summary>
    IReadOnlyList<(Type AggregateType, Type IdType)> RegisteredAggregates { get; }

    void RegisterInfrastructure(Action<IServiceCollection> register);
    void SetPersistenceRegistered();

    bool IsPersistenceRegistered { get; }
}