namespace FEB.EventSourcing.Snapshots;

/// <summary>
/// A pending snapshot write. <paramref name="AggregateType"/> is used for
/// logging/diagnostics only.
/// </summary>
public sealed record SnapshotWriteJob(string AggregateType, Func<CancellationToken, Task> WriteAsync);

/// <summary>
/// Decouples snapshot writes from the command path. When no queue is registered,
/// snapshot stores write inline. Snapshots are an optimization: enqueueing may fail
/// (returns false); dropped jobs are caught up on the next cadence hit.
/// </summary>
public interface ISnapshotWriteQueue
{
    bool TryEnqueue(SnapshotWriteJob job);
}
