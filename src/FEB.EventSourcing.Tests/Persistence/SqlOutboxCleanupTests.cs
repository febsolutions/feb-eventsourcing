using FEB.EventSourcing.Sql;
using FEB.EventSourcing.Tests.Infrastructure;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Persistence;

/// <summary>
/// Retention cleanup of the SQL outbox: completed envelopes older than the retention
/// are deleted, pending and recent ones stay. (MongoDB cleans up server-side via a TTL
/// index - there the index definition is tested, see MongoDbConsistencyTests.)
/// </summary>
public abstract class SqlOutboxCleanupTests
{
    protected abstract ISqlDialect Dialect { get; }
    protected abstract string ConnectionString { get; }

    private async Task<(SqlOutboxPersistence Outbox, SqlEventStoreOptions Options)> BuildAsync(string schema, Action<SqlOutboxOptions>? configure = null)
    {
        var options = new SqlEventStoreOptions();
        options.SetConnectionString(ConnectionString);
        options.SetSchema(schema);

        var outboxOptions = new SqlOutboxOptions();
        configure?.Invoke(outboxOptions);

        await new SqlSchemaInitializer(Dialect, options).EnsureAsync();
        return (new SqlOutboxPersistence(Dialect, options, outboxOptions), options);
    }

    private static OutboxEnvelope Envelope(Guid id) => new(
        new OutboxPayload("t", "{}"),
        new OutboxMetadata(id, "T", "a", 0, DateTime.UtcNow, null, null, "c", null, null));

    private async Task BackdateDispatchedAsync(SqlEventStoreOptions options, Guid id, DateTime dispatchedAt)
    {
        await using var connection = Dialect.CreateConnection(ConnectionString);
        await connection.OpenAsync();
        await SqlEventStorePersistence.ExecuteAsync(connection, null,
            $"UPDATE {options.Qualified(options.OutboxTable)} SET dispatched_at = @d WHERE id = @id",
            CancellationToken.None, ("d", dispatchedAt), ("id", id));
    }

    private async Task<int> CountAsync(SqlEventStoreOptions options)
    {
        await using var connection = Dialect.CreateConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {options.Qualified(options.OutboxTable)}";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Cleanup_removes_only_completed_envelopes_past_retention()
    {
        var (outbox, options) = await BuildAsync("clean1");

        var oldDone = Guid.NewGuid();
        var freshDone = Guid.NewGuid();
        var pending = Guid.NewGuid();
        await outbox.EnqueueAsync(Envelope(oldDone));
        await outbox.EnqueueAsync(Envelope(freshDone));
        await outbox.EnqueueAsync(Envelope(pending));

        await BackdateDispatchedAsync(options, oldDone, DateTime.UtcNow.AddDays(-8));   // older than the retention (7d)
        await BackdateDispatchedAsync(options, freshDone, DateTime.UtcNow.AddHours(-1)); // more recent

        var removed = await outbox.CleanupCompletedAsync();

        removed.Should().Be(1, "only the completed envelope beyond the retention may disappear");
        (await CountAsync(options)).Should().Be(2);
    }

    [Fact]
    public async Task Disabled_cleanup_removes_nothing()
    {
        var (outbox, options) = await BuildAsync("clean2", o => o.DisableCompletedCleanup());

        var id = Guid.NewGuid();
        await outbox.EnqueueAsync(Envelope(id));
        await BackdateDispatchedAsync(options, id, DateTime.UtcNow.AddDays(-30));

        (await outbox.CleanupCompletedAsync()).Should().Be(0);
        (await CountAsync(options)).Should().Be(1);
    }

    [Fact]
    public async Task Cleanup_is_bounded_by_batch_size()
    {
        var (outbox, options) = await BuildAsync("clean3", o => o.SetCleanupBatchSize(2));

        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            await outbox.EnqueueAsync(Envelope(id));
            await BackdateDispatchedAsync(options, id, DateTime.UtcNow.AddDays(-8));
        }

        (await outbox.CleanupCompletedAsync()).Should().Be(2, "bounded delete");
        (await outbox.CleanupCompletedAsync()).Should().Be(2);
        (await outbox.CleanupCompletedAsync()).Should().Be(1);
        (await CountAsync(options)).Should().Be(0);
    }
}

[Collection("postgres")]
public sealed class PostgresOutboxCleanupTests(PostgresFixture fixture) : SqlOutboxCleanupTests
{
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.Postgres.PostgresDialect();
    protected override string ConnectionString => fixture.ConnectionString;
}

[Collection("sqlserver")]
public sealed class SqlServerOutboxCleanupTests(SqlServerFixture fixture) : SqlOutboxCleanupTests
{
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.SqlServer.SqlServerDialect();
    protected override string ConnectionString => fixture.ConnectionString;
}
