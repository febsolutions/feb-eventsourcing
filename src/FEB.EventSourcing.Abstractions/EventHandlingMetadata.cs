namespace FEB.EventSourcing;

public sealed record EventHandlingMetadata(
    Guid EventId,
    string AggregateType,
    int Version,
    DateTime OccurredAt,
    string? TenantId,
    string? UserId,
    string CorrelationId,
    string? CausationId,
    IReadOnlyDictionary<string, string>? Headers
)
{
    public static EventHandlingMetadata From<TId>(EventMetadata<TId> metadata)
        => new(
            metadata.EventId,
            metadata.AggregateType,
            metadata.Version,
            metadata.OccurredAt,
            metadata.TenantId,
            metadata.UserId,
            metadata.CorrelationId,
            metadata.CausationId,
            metadata.Headers);
}