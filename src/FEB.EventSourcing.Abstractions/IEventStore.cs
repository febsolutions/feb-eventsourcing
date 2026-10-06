namespace FEB.EventSourcing;

public interface IEventStore<TAggregate, TId>
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
{
    Task<TAggregate?> LoadByIdAsync(TId id, CancellationToken cancellationToken = default);

    Task SaveAsync(TAggregate aggregate, CommandContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EventEnvelope<TAggregate, TId>>> LoadEventsAsync(
            TId aggregateId,
            int fromVersion = -1,
            CancellationToken cancellationToken = default);

    Task<bool> IsExistsAsync(TId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TId>> GetAllIdsAsync(CancellationToken cancellationToken = default);

    Task RebuildProjectionsAsync(CancellationToken cancellationToken = default);

    [Obsolete("Feature will be removed in upcoming releases", false)]
    Task UpdateProjectionsAsync(TId id, CommandContext context, CancellationToken cancellationToken = default);
    

}