namespace FEB.EventSourcing;

public record EventMetadata<TId>(
    Guid EventId,
    TId AggregateId,
    string AggregateType,
    int Version,
    DateTime OccurredAt,
    string? TenantId,
    string? UserId,
    string CorrelationId,
    string? CausationId,
    IReadOnlyDictionary<string, string>? Headers
);
