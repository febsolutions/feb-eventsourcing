using FEB.EventSourcing.Sql;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Persistence;

/// <summary>
/// On relational stores the outbox envelopes are written in the same transaction as the
/// events: if the outbox write fails, nothing of the append may remain.
/// </summary>
public abstract class SqlAtomicOutboxTests
{
    protected abstract void UseProvider(IEventSourcingBuilder es, string schema);
    protected abstract ISqlDialect Dialect { get; }
    protected abstract string ConnectionString { get; }

    private ServiceProvider BuildHost(string schema)
    {
        var services = new ServiceCollection();
        services.AddEventSourcing(es =>
        {
            UseProvider(es, schema);
            es.UseSqlOutbox();
            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
        }, typeof(TestHost).Assembly);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Failed_outbox_write_rolls_back_the_events()
    {
        var schema = "atomic_" + Guid.NewGuid().ToString("N")[..8];
        await using var sp = BuildHost(schema);
        await sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<SqlSchemaInitializer>().Single().EnsureAsync();

        // Make every outbox write fail.
        var options = sp.GetRequiredService<SqlEventStoreOptions>();
        await using (var connection = Dialect.CreateConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await SqlEventStorePersistence.ExecuteAsync(connection, null,
                $"DROP TABLE {options.Qualified(options.OutboxTable)}", CancellationToken.None);
        }

        var store = sp.GetRequiredService<IEventStore<Order, string>>();
        var id = Guid.NewGuid().ToString("N");
        var order = Order.CreateNew(id);
        order.Create("ACME");

        var save = () => store.SaveAsync(order, TestHost.NewCommandContext());
        await save.Should().ThrowAsync<Exception>("the outbox write failed");

        (await store.LoadEventsAsync(id)).Should().BeEmpty("events and outbox envelopes are one transaction");
        (await store.IsExistsAsync(id)).Should().BeFalse("the version advance is rolled back as well");
    }

    [Fact]
    public async Task EnqueueMany_writes_every_envelope()
    {
        // Used when the SQL outbox is combined with an event persistence of another store.
        var schema = "batch_" + Guid.NewGuid().ToString("N")[..8];
        await using var sp = BuildHost(schema);
        await sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<SqlSchemaInitializer>().Single().EnsureAsync();
        var outbox = sp.GetRequiredService<IOutboxPersistence>();

        var envelopes = Enumerable.Range(0, 3).Select(i => new OutboxEnvelope(
            new OutboxPayload("test-event", "{}"),
            new OutboxMetadata(Guid.NewGuid(), "Order", "agg", i, DateTime.UtcNow, null, null, "c", null, null))).ToList();
        await outbox.EnqueueManyAsync(envelopes);

        var leased = await outbox.DequeueBatchAsync(["s"], 10);
        leased.Select(l => l.Envelope.Metadata.EventId).Should().BeEquivalentTo(envelopes.Select(e => e.Metadata.EventId));
    }

    [Fact]
    public async Task Atomic_append_refuses_an_outbox_of_another_store()
    {
        var schema = "guard_" + Guid.NewGuid().ToString("N")[..8];
        await using var sp = BuildHost(schema);
        var persistence = (IAtomicOutboxAppend)sp.GetRequiredService<IEventStorePersistence>();
        var foreignOutbox = new FEB.EventSourcing.Tests.Core.OutboxEnqueueTests.RecordingOutboxPersistence();

        persistence.SupportsAtomicAppend(foreignOutbox).Should().BeFalse("a different outbox must never be bypassed");
        var act = () => persistence.AppendEventsWithOutboxAsync<Order, string>("x", -1, [], [], foreignOutbox);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

[Collection("postgres")]
public sealed class PostgresAtomicOutboxTests(PostgresFixture fixture) : SqlAtomicOutboxTests
{
    protected override void UseProvider(IEventSourcingBuilder es, string schema)
        => es.UsePostgres(fixture.ConnectionString, o => o.SetSchema(schema));
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.Postgres.PostgresDialect();
    protected override string ConnectionString => fixture.ConnectionString;
}

[Collection("sqlserver")]
public sealed class SqlServerAtomicOutboxTests(SqlServerFixture fixture) : SqlAtomicOutboxTests
{
    protected override void UseProvider(IEventSourcingBuilder es, string schema)
        => es.UseSqlServer(fixture.ConnectionString, o => o.SetSchema(schema));
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.SqlServer.SqlServerDialect();
    protected override string ConnectionString => fixture.ConnectionString;
}
