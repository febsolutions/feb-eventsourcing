namespace FEB.EventSourcing;

public class AggregateFactory<TAggregate, TId>(Func<TId, TAggregate> creator)
    where TAggregate : AggregateRoot<TAggregate>, IEntity<TId>
{
    public TAggregate Create(TId id) => creator(id);
}

