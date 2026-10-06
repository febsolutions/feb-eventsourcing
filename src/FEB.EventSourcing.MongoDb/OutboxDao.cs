using MongoDB.Bson.Serialization.Attributes;

namespace FEB.EventSourcing.MongoDb;

/// <summary>Per-subscriber delivery state of an outbox envelope.</summary>
public sealed class OutboxDeliveryDao
{
    public DateTime? DispatchedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? DeadLetteredAt { get; set; }

    [BsonIgnore]
    public bool IsTerminal => DispatchedAt != null || DeadLetteredAt != null;
}

public class OutboxDao
{
    [BsonId]
    public Guid Id { get; init; }

    public string PayloadJson { get; init; } = default!;
    public string MetadataJson { get; init; } = default!;

    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Completion marker: set once every known subscriber is terminal (dispatched or
    /// dead-lettered). Envelopes written before per-subscriber tracking existed use it
    /// as their only delivery state.
    /// </summary>
    public DateTime? DispatchedAt { get; set; }

    /// <summary>Delivery state per subscriber name. Missing entry = not yet attempted.</summary>
    [BsonIgnoreIfNull]
    public Dictionary<string, OutboxDeliveryDao>? Deliveries { get; set; }

    // Legacy single-consumer fields (pre-subscription envelopes); kept readable.
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    // Lease
    public DateTime? LockedUntil { get; set; }
}
