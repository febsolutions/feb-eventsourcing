namespace FEB.EventSourcing;

internal static class MetaDataExtensions
{
    internal static ProjectionContext ToProjectionContext<TId>(this EventMetadata<TId> metadata)
    {
        return new ProjectionContext(
            metadata.TenantId,
            metadata.UserId,
            metadata.CorrelationId,
            metadata.CausationId ?? metadata.EventId.ToString(),
            metadata.Headers,
            metadata.OccurredAt);
    }
}