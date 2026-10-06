using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FEB.EventSourcing.MongoDb;

/// <summary>
/// Creates the indexes the MongoDB event store relies on for every registered
/// aggregate — at startup, idempotently. Also usable manually via
/// <see cref="EnsureAllAsync"/> (e.g. from a migration step or a test).
/// </summary>
public sealed class MongoDbIndexInitializer(
    IServiceProvider serviceProvider,
    IEventSourcingBuilder builder,
    ILogger<MongoDbIndexInitializer>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<MongoDbIndexInitializer>.Instance;

    public Task StartAsync(CancellationToken cancellationToken) => EnsureAllAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task EnsureAllAsync(CancellationToken cancellationToken = default)
    {
        using var scope = serviceProvider.CreateScope();

        var persistence = scope.ServiceProvider.GetRequiredService<IEventStorePersistence>() as MongoDbEventStorePersistence;
        var outbox = scope.ServiceProvider.GetService<IOutboxPersistence>() as MongoDbOutboxPersistence;

        if (persistence != null)
        {
            var method = typeof(MongoDbEventStorePersistence)
                .GetMethod(nameof(MongoDbEventStorePersistence.EnsureIndexesAsync), BindingFlags.Instance | BindingFlags.Public)!;

            foreach (var (aggregateType, idType) in builder.RegisteredAggregates)
            {
                await (Task)method.MakeGenericMethod(aggregateType, idType).Invoke(persistence, [cancellationToken])!;
                _logger.LogDebug("Ensured event store indexes for {AggregateType}", aggregateType.Name);
            }
        }

        if (outbox != null)
        {
            await outbox.EnsureIndexesAsync(cancellationToken);
            _logger.LogDebug("Ensured outbox indexes");
        }
    }
}
