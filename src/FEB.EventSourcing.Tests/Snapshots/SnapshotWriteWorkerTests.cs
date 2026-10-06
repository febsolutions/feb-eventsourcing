using FEB.EventSourcing.Snapshots;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Snapshots;

public class SnapshotWriteWorkerTests
{
    [Fact]
    public async Task Enqueued_jobs_are_executed_in_the_background()
    {
        var queue = new ChannelSnapshotWriteQueue();
        var worker = new SnapshotWriteWorker(queue);
        var executed = new TaskCompletionSource();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            queue.TryEnqueue(new SnapshotWriteJob("Test", _ =>
            {
                executed.TrySetResult();
                return Task.CompletedTask;
            })).Should().BeTrue();

            await executed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_failing_job_does_not_stop_the_worker()
    {
        var queue = new ChannelSnapshotWriteQueue();
        var worker = new SnapshotWriteWorker(queue);
        var secondExecuted = new TaskCompletionSource();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            queue.TryEnqueue(new SnapshotWriteJob("Broken", _ => throw new InvalidOperationException("boom")));
            queue.TryEnqueue(new SnapshotWriteJob("Ok", _ =>
            {
                secondExecuted.TrySetResult();
                return Task.CompletedTask;
            }));

            await secondExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Full_queue_drops_new_jobs_instead_of_blocking()
    {
        var queue = new ChannelSnapshotWriteQueue(capacity: 1);

        queue.TryEnqueue(new SnapshotWriteJob("a", _ => Task.CompletedTask)).Should().BeTrue();
        queue.TryEnqueue(new SnapshotWriteJob("b", _ => Task.CompletedTask))
            .Should().BeFalse("with a full queue the command path must not block");
    }
}
