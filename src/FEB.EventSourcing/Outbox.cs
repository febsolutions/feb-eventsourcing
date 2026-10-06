namespace FEB.EventSourcing;

public sealed class Outbox(IOutboxPersistence persistence) : IOutbox
{
    public async Task EnqueueAsync<TAggregate, TId>(OutboxEnvelope envelope, CancellationToken cancellationToken) where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        await persistence.EnqueueAsync(envelope, cancellationToken);
    }
}
