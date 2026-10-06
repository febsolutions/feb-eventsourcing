namespace FEB.EventSourcing;

public interface IAggregateIdResolver
{
    bool TryResolve<TId>(object rawAggregateId, out TId id);
}