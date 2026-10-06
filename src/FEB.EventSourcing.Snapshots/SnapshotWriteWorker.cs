using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FEB.EventSourcing.Snapshots;

/// <summary>
/// Drains snapshot write jobs in the background. Failures of individual jobs are
/// logged and break neither the worker nor the command path.
/// </summary>
public sealed class SnapshotWriteWorker(
    ChannelSnapshotWriteQueue queue,
    ILogger<SnapshotWriteWorker>? logger = null) : BackgroundService
{
    private readonly ILogger<SnapshotWriteWorker> _logger = logger ?? NullLogger<SnapshotWriteWorker>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await job.WriteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot write for aggregate {AggregateType} failed", job.AggregateType);
            }
        }
    }
}
