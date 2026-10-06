namespace FEB.EventSourcing;

public sealed class Outbox(IOutboxPersistence persistence) : IOutbox
{
    /// <summary>The persistence behind this outbox; lets the event store write envelopes atomically with the events.</summary>
    internal IOutboxPersistence Persistence => persistence;

    internal Task EnqueueManyAsync(IReadOnlyCollection<OutboxEnvelope> envelopes, CancellationToken cancellationToken)
        => persistence.EnqueueManyAsync(envelopes, cancellationToken);

    public async Task EnqueueAsync<TAggregate, TId>(OutboxEnvelope envelope, CancellationToken cancellationToken) where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        await persistence.EnqueueAsync(envelope, cancellationToken);
    }
}
