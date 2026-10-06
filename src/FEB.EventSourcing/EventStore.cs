namespace FEB.EventSourcing;

public class EventStore<TAggregate, TId>(IEventStore<TAggregate, TId> eventStore) : ProxyStore<TAggregate, TId>(eventStore)
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
{

}