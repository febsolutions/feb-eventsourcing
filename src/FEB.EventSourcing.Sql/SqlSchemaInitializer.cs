using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FEB.EventSourcing.Sql;

/// <summary>
/// Creates schema, tables and indexes idempotently at startup (hosted service) or on
/// demand via <see cref="EnsureAsync"/>. DDL comes from the dialect.
/// </summary>
public sealed class SqlSchemaInitializer(
    ISqlDialect dialect,
    SqlEventStoreOptions options,
    ILogger<SqlSchemaInitializer>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<SqlSchemaInitializer>.Instance;

    public Task StartAsync(CancellationToken cancellationToken) => EnsureAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var statement in dialect.GetSchemaStatements(options))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogDebug("Ensured {Dialect} event store schema '{Schema}'", dialect.Name, options.Schema);
    }
}
