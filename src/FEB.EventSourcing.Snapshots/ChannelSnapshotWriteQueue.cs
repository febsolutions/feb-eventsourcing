using System.Threading.Channels;

namespace FEB.EventSourcing.Snapshots;

/// <summary>
/// Bounded-channel implementation: when the queue is full, new jobs are dropped
/// (snapshots are an optimization — the next cadence hit catches up).
/// </summary>
public sealed class ChannelSnapshotWriteQueue(int capacity = 1024) : ISnapshotWriteQueue
{
    // FullMode.Wait + TryWrite: never blocks, returns false when the queue is full
    // (DropWrite would drop silently and report true).
    private readonly Channel<SnapshotWriteJob> _channel = Channel.CreateBounded<SnapshotWriteJob>(
        new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    internal ChannelReader<SnapshotWriteJob> Reader => _channel.Reader;

    public bool TryEnqueue(SnapshotWriteJob job) => _channel.Writer.TryWrite(job);
}
