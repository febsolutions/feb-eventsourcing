namespace FEB.EventSourcing;

public interface IEventDispatcher
{
    Task DispatchAsync<TAggregate, TId>(
        IReadOnlyCollection<EventEnvelope<TAggregate, TId>> events,
        CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();
}