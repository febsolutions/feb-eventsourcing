using FEB.EventSourcing.Sql;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Persistence;

/// <summary>
/// What ends up in the <c>event_type</c> column: the stable name from [EventName],
/// otherwise the CLR name as before. Both must stay readable.
/// </summary>
public abstract class SqlEventNameTests
{
    protected abstract ISqlDialect Dialect { get; }
    protected abstract string ConnectionString { get; }

    private async Task<(SqlEventStorePersistence Store, SqlEventStoreOptions Options)> BuildAsync(string schema)
    {
        var options = new SqlEventStoreOptions();
        options.SetConnectionString(ConnectionString);
        options.SetSchema(schema);

        await new SqlSchemaInitializer(Dialect, options).EnsureAsync();
        return (new SqlEventStorePersistence(Dialect, options, null), options);
    }

    private async Task<string> ReadEventTypeAsync(SqlEventStoreOptions options, string aggregateId, int version)
    {
        await using var connection = Dialect.CreateConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT event_type FROM {options.Qualified(options.EventsTable)} " +
                          "WHERE aggregate_id = @id AND version = @v";
        SqlEventStorePersistence.AddParameters(cmd, ("id", aggregateId), ("v", version));
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Configured_name_is_stored_and_read_back()
    {
        var (store, options) = await BuildAsync("evname1");
        var id = Guid.NewGuid().ToString("N");

        await store.AppendEventsAsync(id, -1, [TestHost.NewEnvelope(id, 0, new OrderRenamed("ACME"))]);

        (await ReadEventTypeAsync(options, id, 0)).Should().Be("order.renamed",
            "the stable name is stored, not the CLR type - only then may the class move");

        var loaded = await store.LoadEventsAsync<Order, string>(id);
        loaded.Should().ContainSingle().Which.Payload.Should().BeOfType<OrderRenamed>()
            .Which.Customer.Should().Be("ACME");
    }

    [Fact]
    public async Task Events_without_the_attribute_keep_the_clr_name()
    {
        var (store, options) = await BuildAsync("evname2");
        var id = Guid.NewGuid().ToString("N");

        await store.AppendEventsAsync(id, -1, [TestHost.NewEnvelope(id, 0, new OrderCreated("ACME"))]);

        (await ReadEventTypeAsync(options, id, 0)).Should().Be(typeof(OrderCreated).AssemblyQualifiedName,
            "existing applications must not change as long as they do not use the attribute");
    }

    [Fact]
    public async Task Rows_written_before_the_attribute_stay_readable()
    {
        // Older row: the same event still stored under its CLR name.
        var (store, options) = await BuildAsync("evname3");
        var id = Guid.NewGuid().ToString("N");

        await store.AppendEventsAsync(id, -1, [TestHost.NewEnvelope(id, 0, new OrderRenamed("ACME"))]);

        await using var connection = Dialect.CreateConnection(ConnectionString);
        await connection.OpenAsync();
        await SqlEventStorePersistence.ExecuteAsync(connection, null,
            $"UPDATE {options.Qualified(options.EventsTable)} SET event_type = @t WHERE aggregate_id = @id",
            CancellationToken.None, ("t", typeof(OrderRenamed).AssemblyQualifiedName!), ("id", id));

        var loaded = await store.LoadEventsAsync<Order, string>(id);
        loaded.Should().ContainSingle().Which.Payload.Should().BeOfType<OrderRenamed>();
    }

    [Fact]
    public async Task Alias_rows_stay_readable()
    {
        // Data of an event that has already moved: the name no longer exists as a CLR type.
        var (store, options) = await BuildAsync("evname4");
        var id = Guid.NewGuid().ToString("N");

        await store.AppendEventsAsync(id, -1, [TestHost.NewEnvelope(id, 0, new OrderRenamed("ACME"))]);

        await using var connection = Dialect.CreateConnection(ConnectionString);
        await connection.OpenAsync();
        await SqlEventStorePersistence.ExecuteAsync(connection, null,
            $"UPDATE {options.Qualified(options.EventsTable)} SET event_type = @t WHERE aggregate_id = @id",
            CancellationToken.None,
            ("t", "FEB.EventSourcing.Tests.TestDomain.Legacy.OrderRenamedV1, FEB.EventSourcing.Tests"),
            ("id", id));

        var loaded = await store.LoadEventsAsync<Order, string>(id);
        loaded.Should().ContainSingle().Which.Payload.Should().BeOfType<OrderRenamed>();
    }
}

[Collection("postgres")]
public sealed class PostgresEventNameTests(PostgresFixture fixture) : SqlEventNameTests
{
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.Postgres.PostgresDialect();
    protected override string ConnectionString => fixture.ConnectionString;
}

[Collection("sqlserver")]
public sealed class SqlServerEventNameTests(SqlServerFixture fixture) : SqlEventNameTests
{
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.SqlServer.SqlServerDialect();
    protected override string ConnectionString => fixture.ConnectionString;
}
