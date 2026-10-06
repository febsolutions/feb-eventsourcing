using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

internal sealed class EventStoreChainBuilder : IEventStoreChainBuilder
{
    private readonly List<IEventStoreRegistration> _decorators = [];
    private IEventStoreRegistration? _baseRegistration;

    public void SetBase(IEventStoreRegistration registration)
    {
        _baseRegistration = registration;
    }

    public void AddDecorator(IEventStoreRegistration decorator)
    {
        _decorators.Add(decorator);
    }

    public void Build<TAggregate, TId>(IServiceCollection services, EventStoreOptions options)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        if (_baseRegistration == null)
            throw new InvalidOperationException("Base EventStore not set.");

        // Chain order is defined by IEventStoreRegistration.Order (see EventStoreLayer),
        // not by the order of Use*() calls. OrderBy is stable, so equal orders keep
        // registration order — custom layers can slot in anywhere.
        var chain = new List<IEventStoreRegistration> { _baseRegistration };
        chain.AddRange(_decorators.OrderBy(d => d.Order));

        Type innerStoreType = null!;

        foreach (var registration in chain)
        {
            registration.RegisterEventStore<TAggregate, TId>(services, innerStoreType, options);
            innerStoreType = registration.GetRegisteredType<TAggregate, TId>();
        }

        services.AddScoped<IEventStore<TAggregate, TId>>(sp =>
        {
            var innerStore = sp.GetRequiredService(innerStoreType) as IEventStore<TAggregate, TId>
                   ?? throw new InvalidOperationException("Event store chain could not be resolved.");
            return new EventStore<TAggregate, TId>(innerStore);
        });
    }
}
