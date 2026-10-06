namespace FEB.EventSourcing;

public interface IEventStorePersistence
{
    Task AppendEventsAsync<TAggregate, TId>(TId id, int expectedVersion,
        ICollection<EventEnvelope<TAggregate, TId>> events, CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();

    Task<IReadOnlyList<EventEnvelope<TAggregate, TId>>> LoadEventsAsync<TAggregate, TId>(
        TId aggregateId,
        int fromVersion = 0,
        CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();

    Task<bool> IsExistsAsync<TAggregate, TId>(TId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TId>> GetAllIdsAsync<TAggregate, TId>(CancellationToken cancellationToken = default);
}