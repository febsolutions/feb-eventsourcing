namespace FEB.EventSourcing;

public static class EventSourcingAggregateExtensions
{
    public static IEventSourcingBuilder Aggregate<TAggregate, TId>(
        this IEventSourcingBuilder builder,
        Action<AggregateBuilder<TAggregate, TId>> configure)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        var aggregateBuilder = new AggregateBuilder<TAggregate, TId>(builder.Services);
        configure(aggregateBuilder);
        aggregateBuilder.Build(builder.EventStoreChainBuilder);

        if (builder is EventSourcingBuilder concrete)
            concrete.AddRegisteredAggregate(typeof(TAggregate), typeof(TId));

        return builder;
    }
}