using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Sql;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Persistence;

/// <summary>
/// Projection that writes through the caller's transaction — only while a unit of work is
/// active and only for orders of the unit-of-work tests (customer prefix "uow-").
/// </summary>
public sealed class UnitOfWorkProjectionWriter(IServiceProvider services) : IAggregateProjectionWriter<Order>
{
    public const string Table = "uow_projection";
    public const string Prefix = "uow-";

    public async Task UpdateAsync(Order aggregate, ProjectionContext context, CancellationToken cancellationToken)
    {
        var unitOfWork = services.GetService<ISqlUnitOfWork>();
        var options = services.GetService<SqlEventStoreOptions>();
        if (unitOfWork is not { IsActive: true } || options == null || !aggregate.Customer.StartsWith(Prefix, StringComparison.Ordinal))
            return;

        // Plain ADO.NET through the caller's connection and transaction - as in docs/sql.md.
        await using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = $"INSERT INTO {options.Qualified(Table)} (aggregate_id, version, customer) VALUES (@id, @version, @customer)";
        AddParameter(command, "@id", aggregate.Id);
        AddParameter(command, "@version", aggregate.Version);
        AddParameter(command, "@customer", aggregate.Customer);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>
/// Decision 0016: the relational store takes part in a transaction owned by the caller —
/// events, outbox envelopes, snapshots, projections and the caller's own writes commit or
/// roll back together.
/// </summary>
public abstract class SqlUnitOfWorkTests
{
    protected abstract void UseProvider(IEventSourcingBuilder es, string connectionString, string schema);
    protected abstract ISqlDialect Dialect { get; }
    protected abstract string ConnectionString { get; }
    protected abstract string OtherDatabaseConnectionString { get; }
    protected abstract string IsolationLevelQuery { get; }
    protected abstract string RepeatableReadAsReported { get; }
    protected abstract IsolationLevel ConflictIsolationLevel { get; }
    protected abstract Task<string> PrepareConflictDatabaseAsync();

    private sealed record Host(ServiceProvider Provider, SqlEventStoreOptions Options, string ConnectionString) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private async Task<Host> BuildAsync(string? connectionString = null)
    {
        var cs = connectionString ?? ConnectionString;
        var schema = "uow_" + Guid.NewGuid().ToString("N")[..8];
        var services = new ServiceCollection();
        services.AddEventSourcing(es =>
        {
            UseProvider(es, cs, schema);
            es.UseSnapshots();
            es.UseSqlOutbox();
            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
            es.Aggregate<Customer, string>(a =>
            {
                a.Factory(Customer.CreateNew);
                a.Snapshots(s => s.EveryNEvents(2));
            });
        }, typeof(TestHost).Assembly);
        var sp = services.BuildServiceProvider();
        await sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<SqlSchemaInitializer>().Single().EnsureAsync();

        var options = sp.GetRequiredService<SqlEventStoreOptions>();
        await ExecuteAsync(cs, $"CREATE TABLE {options.Qualified("orders_legacy")} (id varchar(100) PRIMARY KEY, customer varchar(200))");
        await ExecuteAsync(cs, $"CREATE TABLE {options.Qualified(UnitOfWorkProjectionWriter.Table)} (aggregate_id varchar(100), version int, customer varchar(200), PRIMARY KEY (aggregate_id, version))");
        return new Host(sp, options, cs);
    }

    private async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = Dialect.CreateConnection(connectionString);
        await connection.OpenAsync();
        await SqlEventStorePersistence.ExecuteAsync(connection, null, sql, CancellationToken.None);
    }

    private async Task<long> CountAsync(Host host, string table)
    {
        await using var connection = Dialect.CreateConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {host.Options.Qualified(table)}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<(DbConnection Connection, DbTransaction Transaction)> BeginAsync(string connectionString, IsolationLevel level = IsolationLevel.ReadCommitted)
    {
        var connection = Dialect.CreateConnection(connectionString);
        await connection.OpenAsync();
        return (connection, await connection.BeginTransactionAsync(level));
    }

    private static Task InsertLegacyAsync(Host host, DbConnection connection, DbTransaction transaction, string id, string customer)
        => SqlEventStorePersistence.ExecuteAsync(connection, transaction,
            $"INSERT INTO {host.Options.Qualified("orders_legacy")} (id, customer) VALUES (@id, @customer)",
            CancellationToken.None, ("id", id), ("customer", customer));

    private static async Task RollbackTolerantlyAsync(ISqlUnitOfWork unitOfWork, DbTransaction transaction)
    {
        unitOfWork.Discarded();
        try { await transaction.RollbackAsync(); }
        catch { /* the database may already have aborted the transaction */ }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task SaveOutsideAsync(Host host, Action<Order> change, string id)
    {
        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
        var order = await store.LoadByIdAsync(id) ?? Order.CreateNew(id);
        change(order);
        await store.SaveAsync(order, TestHost.NewCommandContext());
    }

    // ------------------------------------------------------------------ commit / rollback

    [Fact]
    public async Task Caller_commit_persists_the_table_write_events_envelopes_and_projection()
    {
        await using var host = await BuildAsync();
        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();
        var id = NewId();

        var (connection, transaction) = await BeginAsync(host.ConnectionString);
        await using (connection)
        await using (transaction)
        {
            unitOfWork.Join(connection, transaction);
            await InsertLegacyAsync(host, connection, transaction, id, "uow-acme");

            var order = Order.CreateNew(id);
            order.Create("uow-acme");
            order.AddItem("Widget", 1m);
            await store.SaveAsync(order, TestHost.NewCommandContext());

            connection.State.Should().Be(ConnectionState.Open, "the store must not close the caller's connection");
            await transaction.CommitAsync();
        }
        await unitOfWork.CompletedAsync();

        (await CountAsync(host, "orders_legacy")).Should().Be(1);
        (await CountAsync(host, host.Options.EventsTable)).Should().Be(2);
        (await CountAsync(host, host.Options.VersionsTable)).Should().Be(1);
        (await CountAsync(host, host.Options.OutboxTable)).Should().Be(2);
        (await CountAsync(host, UnitOfWorkProjectionWriter.Table)).Should().Be(1);
    }

    [Fact]
    public async Task Caller_rollback_removes_the_table_write_events_envelopes_and_projection()
    {
        await using var host = await BuildAsync();
        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();
        var id = NewId();

        var (connection, transaction) = await BeginAsync(host.ConnectionString);
        await using (connection)
        await using (transaction)
        {
            unitOfWork.Join(connection, transaction);
            await InsertLegacyAsync(host, connection, transaction, id, "uow-acme");
            var order = Order.CreateNew(id);
            order.Create("uow-acme");
            await store.SaveAsync(order, TestHost.NewCommandContext());

            await RollbackTolerantlyAsync(unitOfWork, transaction);
        }

        foreach (var table in new[] { "orders_legacy", host.Options.EventsTable, host.Options.VersionsTable, host.Options.OutboxTable, UnitOfWorkProjectionWriter.Table })
            (await CountAsync(host, table)).Should().Be(0, $"{table} must be rolled back with the caller");
    }

    [Fact]
    public async Task A_failing_projection_rolls_back_everything_with_the_caller()
    {
        await using var host = await BuildAsync();
        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();
        var id = NewId();

        var (connection, transaction) = await BeginAsync(host.ConnectionString);
        await using (connection)
        await using (transaction)
        {
            unitOfWork.Join(connection, transaction);
            await InsertLegacyAsync(host, connection, transaction, id, "acme");
            var order = Order.CreateNew(id);
            order.Create(FailingOrderProjectionWriter.FailMarker + "uow");

            var save = () => store.SaveAsync(order, TestHost.NewCommandContext());
            await save.Should().ThrowAsync<InvalidOperationException>();
            await RollbackTolerantlyAsync(unitOfWork, transaction);
        }

        (await CountAsync(host, "orders_legacy")).Should().Be(0);
        (await CountAsync(host, host.Options.EventsTable)).Should().Be(0, "inside a unit of work 'stored but not projected' cannot happen");
        (await CountAsync(host, host.Options.OutboxTable)).Should().Be(0);
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task Version_conflict_keeps_the_transaction_usable_and_a_retry_succeeds()
    {
        await using var host = await BuildAsync();
        var id = NewId();
        await SaveOutsideAsync(host, o => o.Create("uow-acme"), id);                 // v0

        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();

        var (connection, transaction) = await BeginAsync(host.ConnectionString);
        await using (connection)
        await using (transaction)
        {
            unitOfWork.Join(connection, transaction);
            var copy = await store.LoadByIdAsync(id);

            await SaveOutsideAsync(host, o => o.AddItem("Gadget", 2m), id);          // v1 by another writer

            copy!.AddItem("Widget", 1m);
            var save = () => store.SaveAsync(copy, TestHost.NewCommandContext());
            var conflict = (await save.Should().ThrowAsync<ConcurrencyException<string>>()).Which;
            conflict.InnerException.Should().BeNull("this is the version check (zero rows), not a database error");

            await using (var probe = connection.CreateCommand())
            {
                probe.Transaction = transaction;
                probe.CommandText = "SELECT 1";
                await probe.ExecuteScalarAsync();                                     // still usable
            }
            await RollbackTolerantlyAsync(unitOfWork, transaction);
        }

        // Retry: a new unit of work in the same scope with a reloaded aggregate.
        var (retryConnection, retryTransaction) = await BeginAsync(host.ConnectionString);
        await using (retryConnection)
        await using (retryTransaction)
        {
            unitOfWork.Join(retryConnection, retryTransaction);
            var reloaded = await store.LoadByIdAsync(id);
            reloaded!.AddItem("Widget", 1m);
            await store.SaveAsync(reloaded, TestHost.NewCommandContext());
            await retryTransaction.CommitAsync();
        }
        await unitOfWork.CompletedAsync();

        (await CountAsync(host, host.Options.EventsTable)).Should().Be(3);
    }

    [Fact]
    public async Task Conflict_under_a_stricter_isolation_level_is_a_concurrency_exception()
    {
        var connectionString = await PrepareConflictDatabaseAsync();
        await using var host = await BuildAsync(connectionString);
        var id = NewId();
        await SaveOutsideAsync(host, o => o.Create("uow-acme"), id);                 // v0

        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();

        var (connection, transaction) = await BeginAsync(connectionString, ConflictIsolationLevel);
        await using (connection)
        await using (transaction)
        {
            unitOfWork.Join(connection, transaction);
            var copy = await store.LoadByIdAsync(id);                                 // establishes the snapshot

            await SaveOutsideAsync(host, o => o.AddItem("Gadget", 2m), id);          // concurrent writer commits v1

            copy!.AddItem("Widget", 1m);
            var save = () => store.SaveAsync(copy, TestHost.NewCommandContext());
            var conflict = (await save.Should().ThrowAsync<ConcurrencyException<string>>()).Which;
            conflict.InnerException.Should().BeAssignableTo<DbException>("the database reported the conflict as an error");

            // The database may have aborted the transaction; the documented rollback tolerates it.
            await RollbackTolerantlyAsync(unitOfWork, transaction);
        }

        (await CountAsync(host, host.Options.EventsTable)).Should().Be(2, "only the concurrent writer's events remain");
    }

    [Fact]
    public async Task Two_saves_of_one_aggregate_in_one_unit_of_work_see_each_other_without_deadlock()
    {
        await using var host = await BuildAsync();
        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();
        var id = NewId();

        var (connection, transaction) = await BeginAsync(host.ConnectionString);
        await using (connection)
        await using (transaction)
        {
            unitOfWork.Join(connection, transaction);
            var work = async () =>
            {
                var order = Order.CreateNew(id);
                order.Create("uow-acme");
                await store.SaveAsync(order, TestHost.NewCommandContext());

                var reloaded = await store.LoadByIdAsync(id);                        // sees its own uncommitted events
                reloaded!.Version.Should().Be(0);
                reloaded.AddItem("Widget", 1m);
                await store.SaveAsync(reloaded, TestHost.NewCommandContext());
            };
            await work().WaitAsync(TimeSpan.FromSeconds(60));
            await transaction.CommitAsync();
        }
        await unitOfWork.CompletedAsync();

        (await CountAsync(host, host.Options.EventsTable)).Should().Be(2);
    }

    [Fact]
    public async Task The_callers_isolation_level_is_kept()
    {
        await using var host = await BuildAsync();
        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();

        var (connection, transaction) = await BeginAsync(host.ConnectionString, IsolationLevel.RepeatableRead);
        await using (connection)
        await using (transaction)
        {
            unitOfWork.Join(connection, transaction);
            var order = Order.CreateNew(NewId());
            order.Create("uow-acme");
            await store.SaveAsync(order, TestHost.NewCommandContext());

            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = IsolationLevelQuery;
            Convert.ToString(await query.ExecuteScalarAsync()).Should().Be(RepeatableReadAsReported);

            await RollbackTolerantlyAsync(unitOfWork, transaction);
        }
    }

    // ------------------------------------------------------------------ snapshots

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Inline_snapshots_commit_and_roll_back_with_the_caller(bool commit, int expectedSnapshots)
    {
        await using var host = await BuildAsync();
        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Customer, string>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();

        var (connection, transaction) = await BeginAsync(host.ConnectionString);
        await using (connection)
        await using (transaction)
        {
            unitOfWork.Join(connection, transaction);
            var customer = Customer.CreateNew(NewId());
            customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
            customer.Relocate("Hauptstraße 1", "Berlin");                           // 2 events: cadence crossed
            await store.SaveAsync(customer, TestHost.NewCommandContext());

            if (commit)
                await transaction.CommitAsync();
            else
                await RollbackTolerantlyAsync(unitOfWork, transaction);
        }
        if (commit)
            await unitOfWork.CompletedAsync();

        (await CountAsync(host, host.Options.SnapshotsTable)).Should().Be(expectedSnapshots,
            "a snapshot written beside the caller's transaction would outlive its rollback");
    }

    // ------------------------------------------------------------------ joining and lifecycle

    [Fact]
    public async Task Join_rejects_another_database_and_a_concurrent_unit_of_work()
    {
        await using var host = await BuildAsync();
        using var scope = host.Provider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();

        var (other, otherTransaction) = await BeginAsync(OtherDatabaseConnectionString);
        await using (other)
        await using (otherTransaction)
        {
            var joinOther = () => unitOfWork.Join(other, otherTransaction);
            joinOther.Should().Throw<ArgumentException>().WithMessage("*database*");
            await otherTransaction.RollbackAsync();
        }

        var (first, firstTransaction) = await BeginAsync(host.ConnectionString);
        var (second, secondTransaction) = await BeginAsync(host.ConnectionString);
        await using (first) await using (firstTransaction) await using (second) await using (secondTransaction)
        {
            unitOfWork.Join(first, firstTransaction);
            var joinAgain = () => unitOfWork.Join(second, secondTransaction);
            joinAgain.Should().Throw<InvalidOperationException>();

            await RollbackTolerantlyAsync(unitOfWork, firstTransaction);
            await secondTransaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Consecutive_units_of_work_in_one_scope_both_commit()
    {
        await using var host = await BuildAsync();
        using var scope = host.Provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();

        for (var i = 0; i < 2; i++)                                                   // e.g. one unit per aggregate in a loop
        {
            var (connection, transaction) = await BeginAsync(host.ConnectionString);
            await using (connection)
            await using (transaction)
            {
                unitOfWork.Join(connection, transaction);
                var order = Order.CreateNew(NewId());
                order.Create("uow-acme");
                await store.SaveAsync(order, TestHost.NewCommandContext());
                await transaction.CommitAsync();
            }
            await unitOfWork.CompletedAsync();
        }

        (await CountAsync(host, host.Options.EventsTable)).Should().Be(2);
    }

    [Fact]
    public async Task A_scope_that_ends_with_an_active_unit_of_work_discards_it()
    {
        await using var host = await BuildAsync();
        var deferredRan = false;
        SqlUnitOfWork unitOfWork;

        var (connection, transaction) = await BeginAsync(host.ConnectionString);
        await using (connection)
        await using (transaction)
        {
            using (var scope = host.Provider.CreateScope())
            {
                unitOfWork = scope.ServiceProvider.GetRequiredService<SqlUnitOfWork>();
                unitOfWork.Join(connection, transaction);
                await unitOfWork.AfterCommitAsync(_ => { deferredRan = true; return Task.CompletedTask; });
            }                                                                          // never completed

            await transaction.RollbackAsync();
        }

        unitOfWork.IsActive.Should().BeFalse("the scope's end discards an unfinished unit of work");
        deferredRan.Should().BeFalse("whether the caller committed is unknown - deferred actions are dropped");
    }

    [Fact]
    public async Task Persistences_built_with_the_previous_constructors_keep_working()
    {
        // Compatibility: code that constructs the persistences itself keeps compiling and
        // running - without a unit of work, on connections of their own.
        await using var host = await BuildAsync();
        var serializer = host.Provider.GetRequiredService<ISnapshotSerializer>();
        var events = new SqlEventStorePersistence(Dialect, host.Options, null);
        var snapshots = new SqlSnapshotPersistence(Dialect, host.Options, serializer, null);
        var id = NewId();

        await events.AppendEventsAsync(id, -1, [TestHost.NewEnvelope(id, 0, new OrderCreated("uow-acme"))]);
        (await events.LoadEventsAsync<Order, string>(id)).Should().ContainSingle();
        (await events.IsExistsAsync<Order, string>(id)).Should().BeTrue();

        var snapshot = new { Name = "x" };
        await snapshots.SaveAsync<Customer, string>(snapshot, snapshot.GetType(), id, 3, 1);
        (await CountAsync(host, host.Options.SnapshotsTable)).Should().Be(1);
        snapshots.ForBackgroundWork().Should().NotBeSameAs(snapshots, "background work gets a detached instance");
    }

    [Fact]
    public async Task CompletedAsync_never_throws_and_runs_the_remaining_actions()
    {
        await using var host = await BuildAsync();
        using var scope = host.Provider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ISqlUnitOfWork>();
        var secondRan = false;

        var (connection, transaction) = await BeginAsync(host.ConnectionString);
        await using (connection)
        await using (transaction)
        {
            unitOfWork.Join(connection, transaction);
            await unitOfWork.AfterCommitAsync(_ => throw new InvalidOperationException("cache unavailable"));
            await unitOfWork.AfterCommitAsync(_ => { secondRan = true; return Task.CompletedTask; });
            await transaction.CommitAsync();
        }

        var complete = () => unitOfWork.CompletedAsync();
        await complete.Should().NotThrowAsync("the data is committed; post-commit work is best effort");
        secondRan.Should().BeTrue();
        await complete.Should().NotThrowAsync("completing without an active unit of work is a no-op");
    }
}

[Collection("postgres")]
public sealed class PostgresUnitOfWorkTests(PostgresFixture fixture) : SqlUnitOfWorkTests
{
    protected override void UseProvider(IEventSourcingBuilder es, string connectionString, string schema)
        => es.UsePostgres(connectionString, o => { o.SetSchema(schema); o.EnableSnapshots(); });
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.Postgres.PostgresDialect();
    protected override string ConnectionString => fixture.ConnectionString;
    protected override string OtherDatabaseConnectionString
        => new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "template1" }.ConnectionString;
    protected override string IsolationLevelQuery => "SHOW transaction_isolation";
    protected override string RepeatableReadAsReported => "repeatable read";
    protected override IsolationLevel ConflictIsolationLevel => IsolationLevel.RepeatableRead;
    protected override Task<string> PrepareConflictDatabaseAsync() => Task.FromResult(fixture.ConnectionString);
}

[Collection("sqlserver")]
public sealed class SqlServerUnitOfWorkTests(SqlServerFixture fixture) : SqlUnitOfWorkTests
{
    private const string SnapshotDatabase = "es_uow_snapshot";

    protected override void UseProvider(IEventSourcingBuilder es, string connectionString, string schema)
        => es.UseSqlServer(connectionString, o => { o.SetSchema(schema); o.EnableSnapshots(); });
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.SqlServer.SqlServerDialect();
    protected override string ConnectionString => fixture.ConnectionString;
    protected override string OtherDatabaseConnectionString
        => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = "tempdb" }.ConnectionString;
    protected override string IsolationLevelQuery
        => "SELECT CAST(transaction_isolation_level AS int) FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
    protected override string RepeatableReadAsReported => "3";
    protected override IsolationLevel ConflictIsolationLevel => IsolationLevel.Snapshot;

    // SNAPSHOT isolation must be allowed on the database; done on a dedicated one.
    protected override async Task<string> PrepareConflictDatabaseAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await SqlEventStorePersistence.ExecuteAsync(connection, null,
            $"IF DB_ID('{SnapshotDatabase}') IS NULL CREATE DATABASE {SnapshotDatabase}", CancellationToken.None);
        await SqlEventStorePersistence.ExecuteAsync(connection, null,
            $"ALTER DATABASE {SnapshotDatabase} SET ALLOW_SNAPSHOT_ISOLATION ON", CancellationToken.None);
        return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = SnapshotDatabase }.ConnectionString;
    }
}
