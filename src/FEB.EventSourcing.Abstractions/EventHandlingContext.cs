namespace FEB.EventSourcing;

public sealed class EventHandlingContext
{
    public Guid EventId { get; }
    public string AggregateType { get; }
    public int Version { get; }
    public DateTime OccurredAt { get; }
    public string? TenantId { get; }
    public string? UserId { get; }
    public string CorrelationId { get; }
    public string? CausationId { get; }
    public IReadOnlyDictionary<string, string>? Headers { get; }

    private readonly object _aggregateId;

    internal EventHandlingContext(
        object aggregateId,
        EventHandlingMetadata metadata,
        IAggregateIdResolver? aggregateIdResolver)
    {
        _aggregateId = aggregateId;
        EventId = metadata.EventId;
        AggregateType = metadata.AggregateType;
        Version = metadata.Version;
        OccurredAt = metadata.OccurredAt;
        TenantId = metadata.TenantId;
        UserId = metadata.UserId;
        CorrelationId = metadata.CorrelationId;
        CausationId = metadata.CausationId;
        Headers = metadata.Headers;
    }
    
    public static EventHandlingContext Create<TId>(
        TId aggregateId,
        EventMetadata<TId> metadata)
    {
        return new EventHandlingContext(
            aggregateId,
            EventHandlingMetadata.From(metadata),
            null);
    }
    
    public static EventHandlingContext CreateFromOutbox(
        string aggregateId,
        OutboxMetadata metadata,
        IAggregateIdResolver resolver)
    {
        return new EventHandlingContext(
            aggregateId,
            new EventHandlingMetadata(
                EventId: metadata.EventId,
                AggregateType: metadata.AggregateType,
                Version: metadata.Version,
                OccurredAt: metadata.OccurredAt,
                TenantId: metadata.TenantId,
                UserId: metadata.UserId,
                CorrelationId: metadata.CorrelationId,
                CausationId: metadata.CausationId,
                Headers: metadata.Headers
            ),
            resolver);
    }

    public TId GetAggregateId<TId>()
        => _aggregateId is TId id
            ? id
            : throw new InvalidOperationException(
                $"AggregateId is not of type {typeof(TId).Name}");
}