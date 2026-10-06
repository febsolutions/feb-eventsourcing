namespace FEB.EventSourcing.MongoDb;

public sealed class MongoOutboxOptions
{
    internal string OutboxCollectionName { get; private set; } = "outbox";
    
    internal string DeadLetterCollectionName { get; private set; } = "outbox_deadletter";
    
    internal int MaxAttempts { get; private set; } = 10;
    
    internal TimeSpan LeaseDuration { get; private set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long completed envelopes are kept (TTL index on DispatchedAt); null = keep forever.</summary>
    internal TimeSpan? CompletedRetention { get; private set; } = TimeSpan.FromDays(7);
    
    public void SetOutboxCollectionName(string name) 
        => OutboxCollectionName = name;
    
    public void SetLeaseDuration(TimeSpan duration)
        => LeaseDuration = duration;
    
    public void SetMaxAttempts(int attempts)
        => MaxAttempts = attempts;
    
    public void SetDeadLetterCollectionName(string name)
        => DeadLetterCollectionName = name;

    /// <summary>Retention for completed envelopes (default 7 days). MongoDB enforces it via a TTL index.</summary>
    public void SetCompletedRetention(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        CompletedRetention = retention;
    }

    /// <summary>Keep completed envelopes forever (pre-9.0.0-beta.3 behavior).</summary>
    public void DisableCompletedCleanup() => CompletedRetention = null;
}