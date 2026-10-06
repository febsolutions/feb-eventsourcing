namespace FEB.EventSourcing;

public interface IDomainEvent<in T> : IEvent
    where T : IEntity
{
    void ApplyTo(T obj);
}