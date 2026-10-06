namespace FEB.EventSourcing;

public interface IEventProjectionWriter<out TAggregate, in TId> : IProjectionWriter
    where TAggregate : IEntity<TId>
{
    Task ApplyAsync(
        TId aggregateId,
        IReadOnlyCollection<IDomainEvent<TAggregate>> events,
        ProjectionContext context,
        CancellationToken cancellationToken);
}