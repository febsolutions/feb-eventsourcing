namespace FEB.EventSourcing;

public record EventEnvelope<TAggregate, TId>(IDomainEvent<TAggregate> Payload, EventMetadata<TId> Metadata)
    where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new();
