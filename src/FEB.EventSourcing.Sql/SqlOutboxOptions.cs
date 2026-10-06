namespace FEB.EventSourcing.Sql;

public sealed class SqlOutboxOptions
{
    internal int MaxAttempts { get; private set; } = 10;

    internal TimeSpan LeaseDuration { get; private set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long completed envelopes are kept; null = keep forever.</summary>
    internal TimeSpan? CompletedRetention { get; private set; } = TimeSpan.FromDays(7);

    internal int CleanupBatchSize { get; private set; } = 1000;

    public void SetMaxAttempts(int attempts)
    {
        if (attempts <= 0) throw new ArgumentOutOfRangeException(nameof(attempts));
        MaxAttempts = attempts;
    }

    public void SetLeaseDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        LeaseDuration = duration;
    }

    /// <summary>Retention for completed envelopes (default 7 days); enforced by the worker's periodic cleanup.</summary>
    public void SetCompletedRetention(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        CompletedRetention = retention;
    }

    /// <summary>Keep completed envelopes forever (pre-9.0.0-beta.3 behavior).</summary>
    public void DisableCompletedCleanup() => CompletedRetention = null;

    /// <summary>Max rows removed per cleanup run (bounded deletes; default 1000).</summary>
    public void SetCleanupBatchSize(int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        CleanupBatchSize = size;
    }
}
