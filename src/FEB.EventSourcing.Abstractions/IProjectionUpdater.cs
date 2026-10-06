namespace FEB.EventSourcing;

public interface IProjectionUpdater
{
    Task UpdateAsync<TAggregate, TId>(
        ProjectionUpdateContext<TAggregate, TId> context,
        CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();
}

