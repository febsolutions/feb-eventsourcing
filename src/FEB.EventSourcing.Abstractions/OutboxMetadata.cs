namespace FEB.EventSourcing;

public sealed record OutboxMetadata(
    Guid EventId,
    string AggregateType,
    string AggregateId,
    int Version,
    DateTime OccurredAt,
    string? TenantId,
    string? UserId,
    string CorrelationId,
    string? CausationId,
    IReadOnlyDictionary<string, string>? Headers
);