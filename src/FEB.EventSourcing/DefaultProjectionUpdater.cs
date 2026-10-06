using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing;

public sealed class DefaultProjectionUpdater(IServiceProvider serviceProvider) : IProjectionUpdater
{
    public async Task UpdateAsync<TAggregate, TId>(
        ProjectionUpdateContext<TAggregate, TId> context,
        CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        var aggregateProjectionWriter = serviceProvider.GetServices<IAggregateProjectionWriter<TAggregate>>();
        var eventProjectionWriter = serviceProvider.GetServices<IEventProjectionWriter<TAggregate, TId>>();

        foreach (var writer in aggregateProjectionWriter)
            await writer.UpdateAsync(
                context.Aggregate,
                context.Context,
                cancellationToken);

        foreach (var writer in eventProjectionWriter)
            await writer.ApplyAsync(
                context.Aggregate.Id,
                context.NewEvents,
                context.Context,
                cancellationToken);
    }
}