namespace FEB.EventSourcing;

/// <summary>
/// Base class for event store decorators: forwards everything to the inner store,
/// override what the layer adds.
/// </summary>
public class ProxyStore<TAggregate, TId>(IEventStore<TAggregate, TId> innerStore) : IEventStore<TAggregate, TId>
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
{
    protected IEventStore<TAggregate, TId> InnerStore { get; } = innerStore ?? throw new ArgumentNullException(nameof(innerStore));

    public virtual Task<TAggregate?> LoadByIdAsync(TId id, CancellationToken cancellationToken = default)
        => InnerStore.LoadByIdAsync(id, cancellationToken);

    public virtual Task SaveAsync(TAggregate aggregate, CommandContext context, CancellationToken cancellationToken = default)
        => InnerStore.SaveAsync(aggregate, context, cancellationToken);

    public virtual Task<IReadOnlyList<EventEnvelope<TAggregate, TId>>> LoadEventsAsync(TId aggregateId, int fromVersion = -1, CancellationToken cancellationToken = default)
        => InnerStore.LoadEventsAsync(aggregateId, fromVersion, cancellationToken);

    public virtual Task<bool> IsExistsAsync(TId id, CancellationToken cancellationToken = default)
        => InnerStore.IsExistsAsync(id, cancellationToken);

    public virtual Task<IReadOnlyList<TId>> GetAllIdsAsync(CancellationToken cancellationToken = default)
        => InnerStore.GetAllIdsAsync(cancellationToken);

    public virtual Task RebuildProjectionsAsync(CancellationToken cancellationToken = default)
        => InnerStore.RebuildProjectionsAsync(cancellationToken);

    public virtual Task UpdateProjectionsAsync(TId id, CommandContext context, CancellationToken cancellationToken = default)
        => InnerStore.UpdateProjectionsAsync(id, context, cancellationToken);
}
