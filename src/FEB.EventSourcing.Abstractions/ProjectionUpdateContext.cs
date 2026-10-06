namespace FEB.EventSourcing;

public sealed class ProjectionUpdateContext<TAggregate, TId>(
    TAggregate aggregate,
    IReadOnlyCollection<IDomainEvent<TAggregate>> newEvents,
    ProjectionContext context)
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
{
    public TAggregate Aggregate { get; } = aggregate;
    
    public IReadOnlyCollection<IDomainEvent<TAggregate>> NewEvents { get; } = newEvents;

    public ProjectionContext Context { get; } = context;
}