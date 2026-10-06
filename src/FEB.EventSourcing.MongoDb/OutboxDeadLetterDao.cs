using MongoDB.Bson.Serialization.Attributes;

namespace FEB.EventSourcing.MongoDb;

/// <summary>A delivery that exhausted MaxAttempts for one subscriber. Id = "{EventId}:{Subscriber}".</summary>
public sealed class OutboxDeadLetterDao
{
    [BsonId]
    public string Id { get; init; } = default!;

    public Guid EventId { get; init; }
    public string Subscriber { get; init; } = default!;

    public string PayloadJson { get; init; } = default!;
    public string MetadataJson { get; init; } = default!;

    public DateTime CreatedAt { get; init; }

    public int AttemptCount { get; init; }
    public DateTime FailedAt { get; init; }
    public string LastError { get; init; } = default!;
}
