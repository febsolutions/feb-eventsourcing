namespace FEB.EventSourcing;

public interface IAggregateProjectionWriter<in TAggregate> : IProjectionWriter
{
    Task UpdateAsync(
        TAggregate aggregate,
        ProjectionContext context,
        CancellationToken cancellationToken);
}