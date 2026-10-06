namespace FEB.EventSourcing;

public interface IOutbox
{
    Task EnqueueAsync<TAggregate, TId>(
        OutboxEnvelope envelope,
        CancellationToken cancellationToken)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();
}