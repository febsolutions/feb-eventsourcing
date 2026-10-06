namespace FEB.EventSourcing;

/// <summary>
/// Optional capability of an <see cref="IEventStorePersistence"/>: writing the outbox
/// envelopes of an append in the same atomic operation as the events, so a stored event
/// can never miss its envelope. The event store uses it whenever the registered outbox
/// lives in the same store; otherwise it appends first and enqueues immediately
/// afterwards. Persistences that do not implement it keep working unchanged.
/// </summary>
public interface IAtomicOutboxAppend
{
    /// <summary>
    /// True if envelopes for <paramref name="outbox"/> can be written atomically with the
    /// events — the outbox lives in the same store and the store supports it in its
    /// current configuration.
    /// </summary>
    bool SupportsAtomicAppend(IOutboxPersistence outbox);

    /// <summary>
    /// Appends <paramref name="events"/> with the same concurrency semantics as
    /// <see cref="IEventStorePersistence.AppendEventsAsync{TAggregate, TId}"/> and enqueues
    /// <paramref name="outboxEnvelopes"/> to <paramref name="outbox"/>, all or nothing.
    /// </summary>
    Task AppendEventsWithOutboxAsync<TAggregate, TId>(
        TId id,
        int expectedVersion,
        ICollection<EventEnvelope<TAggregate, TId>> events,
        IReadOnlyCollection<OutboxEnvelope> outboxEnvelopes,
        IOutboxPersistence outbox,
        CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();
}
