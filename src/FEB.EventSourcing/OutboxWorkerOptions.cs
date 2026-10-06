namespace FEB.EventSourcing;

public sealed class OutboxWorkerOptions
{
    internal int BatchSize { get; private set; } = 50;

    internal TimeSpan PollingInterval { get; private set; } = TimeSpan.FromSeconds(1);

    internal TimeSpan ErrorBackoff { get; private set; } = TimeSpan.FromSeconds(2);

    internal TimeSpan CleanupInterval { get; private set; } = TimeSpan.FromMinutes(5);

    public void SetBatchSize(int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        BatchSize = size;
    }

    public void SetPollingInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        PollingInterval = interval;
    }

    /// <summary>Delay after an unexpected loop failure (protects against hot loops).</summary>
    public void SetErrorBackoff(TimeSpan backoff)
    {
        if (backoff <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(backoff));
        ErrorBackoff = backoff;
    }

    /// <summary>How often the worker triggers <c>IOutboxPersistence.CleanupCompletedAsync</c> (default 5 min).</summary>
    public void SetCleanupInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        CleanupInterval = interval;
    }
}
