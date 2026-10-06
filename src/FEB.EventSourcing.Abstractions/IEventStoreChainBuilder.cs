using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

public interface IEventStoreChainBuilder
{
    void SetBase(IEventStoreRegistration registration);

    void AddDecorator(IEventStoreRegistration decorator);

    void Build<TAggregate, TId>(IServiceCollection services, EventStoreOptions options)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();
}