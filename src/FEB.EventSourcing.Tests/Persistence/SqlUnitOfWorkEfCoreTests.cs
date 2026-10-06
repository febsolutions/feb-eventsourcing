using AwesomeAssertions;
using FEB.EventSourcing.Sql;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Persistence;

public sealed class LegacyOrder
{
    public string Id { get; set; } = "";
    public string Customer { get; set; } = "";
}

public sealed class ProjectedOrder
{
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public string Customer { get; set; } = "";
}

/// <summary>An existing application's DbContext: its own table plus a projection table.</summary>
public sealed class LegacyDbContext(DbContextOptions<LegacyDbContext> options) : DbContext(options)
{
    public const string Schema = "uow_ef";

    public DbSet<LegacyOrder> Orders => Set<LegacyOrder>();
    public DbSet<ProjectedOrder> Projected => Set<ProjectedOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<LegacyOrder>(e => { e.ToTable("legacy_orders"); e.HasKey(x => x.Id); });
        modelBuilder.Entity<ProjectedOrder>(e => { e.ToTable("projected_orders"); e.HasKey(x => x.Id); });
    }
}

/// <summary>
/// Projection writing through the caller's DbContext — only inside its transaction and only
/// for orders of the EF Core tests (customer prefix "ef-"). It must save its changes itself.
/// </summary>
public sealed class EfOrderProjectionWriter(IServiceProvider services) : IAggregateProjectionWriter<Order>
{
    public async Task UpdateAsync(Order aggregate, ProjectionContext context, CancellationToken cancellationToken)
    {
        var db = services.GetService<LegacyDbContext>();
        if (db?.Database.CurrentTransaction == null || !aggregate.Customer.StartsWith("ef-", StringComparison.Ordinal))
            return;

        db.Projected.Add(new ProjectedOrder { Id = aggregate.Id, Version = aggregate.Version, Customer = aggregate.Customer });
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// The documented integration pattern (decision 0016): an EF Core transaction inside the
/// execution strategy (EnableRetryOnFailure), joined by the event store, with the
/// application's table write, the events and a DbContext-based projection committing or
/// rolling back together.
/// </summary>
public abstract class SqlUnitOfWorkEfCoreTests
{
    protected abstract void UseProvider(IEventSourcingBuilder es, string schema);
    protected abstract void UseDatabase(DbContextOptionsBuilder options);
    protected abstract ISqlDialect Dialect { get; }
    protected abstract string ConnectionString { get; }
    protected abstract string CreateTablesSql { get; }

    private async Task<ServiceProvider> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<LegacyDbContext>(UseDatabase);
        services.AddEventSourcing(es =>
        {
            UseProvider(es, LegacyDbContext.Schema);
            es.UseSqlOutbox();
            es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
        }, typeof(TestHost).Assembly);
        var sp = services.BuildServiceProvider();
        await sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<SqlSchemaInitializer>().Single().EnsureAsync();

        await using var connection = Dialect.CreateConnection(ConnectionString);
        await connection.OpenAsync();
        await SqlEventStorePersistence.ExecuteAsync(connection, null, CreateTablesSql, CancellationToken.None);
        return sp;
    }

    /// <summary>The pattern from decision 0016, verbatim apart from the commit switch.</summary>
    private static async Task RunInUnitOfWorkAsync(IServiceProvider services, Func<LegacyDbContext, Task> work, bool commit)
    {
        var db = services.GetRequiredService<LegacyDbContext>();
        var unitOfWork = services.GetRequiredService<ISqlUnitOfWork>();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync();
            unitOfWork.Join(db.Database.GetDbConnection(), transaction.GetDbTransaction());
            try
            {
                await work(db);
                if (!commit)
                {
                    unitOfWork.Discarded();
                    await transaction.RollbackAsync();
                    return;
                }
                await transaction.CommitAsync();
            }
            catch
            {
                unitOfWork.Discarded();
                try { await transaction.RollbackAsync(); }
                catch { /* the database may already have aborted the transaction */ }
                throw;
            }

            await unitOfWork.CompletedAsync();
        });
    }

    private async Task<(int Legacy, int Projected, int Events)> CountAsync(ServiceProvider sp, string id)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LegacyDbContext>();
        var events = await scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>().LoadEventsAsync(id);
        return (await db.Orders.CountAsync(o => o.Id == id), await db.Projected.CountAsync(o => o.Id == id), events.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EF_Core_write_events_and_DbContext_projection_share_one_transaction(bool commit)
    {
        await using var sp = await BuildAsync();
        var id = Guid.NewGuid().ToString("N");

        using (var scope = sp.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IEventStore<Order, string>>();
            await RunInUnitOfWorkAsync(scope.ServiceProvider, async db =>
            {
                db.Orders.Add(new LegacyOrder { Id = id, Customer = "ef-acme" });   // existing table write
                await db.SaveChangesAsync();

                var order = Order.CreateNew(id);
                order.Create("ef-acme");
                await store.SaveAsync(order, TestHost.NewCommandContext());        // events + DbContext projection
            }, commit);
        }

        var expected = commit ? 1 : 0;
        (await CountAsync(sp, id)).Should().Be((expected, expected, expected),
            commit ? "everything commits together" : "everything rolls back together");
    }
}

[Collection("postgres")]
public sealed class PostgresUnitOfWorkEfCoreTests(PostgresFixture fixture) : SqlUnitOfWorkEfCoreTests
{
    protected override void UseProvider(IEventSourcingBuilder es, string schema)
        => es.UsePostgres(fixture.ConnectionString, o => o.SetSchema(schema));
    protected override void UseDatabase(DbContextOptionsBuilder options)
        => options.UseNpgsql(fixture.ConnectionString, o => o.EnableRetryOnFailure());
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.Postgres.PostgresDialect();
    protected override string ConnectionString => fixture.ConnectionString;
    protected override string CreateTablesSql =>
        $"CREATE TABLE IF NOT EXISTS {LegacyDbContext.Schema}.legacy_orders (\"Id\" varchar(100) PRIMARY KEY, \"Customer\" varchar(200) NOT NULL); " +
        $"CREATE TABLE IF NOT EXISTS {LegacyDbContext.Schema}.projected_orders (\"Id\" varchar(100) PRIMARY KEY, \"Version\" int NOT NULL, \"Customer\" varchar(200) NOT NULL);";
}

[Collection("sqlserver")]
public sealed class SqlServerUnitOfWorkEfCoreTests(SqlServerFixture fixture) : SqlUnitOfWorkEfCoreTests
{
    protected override void UseProvider(IEventSourcingBuilder es, string schema)
        => es.UseSqlServer(fixture.ConnectionString, o => o.SetSchema(schema));
    protected override void UseDatabase(DbContextOptionsBuilder options)
        => options.UseSqlServer(fixture.ConnectionString, o => o.EnableRetryOnFailure());
    protected override ISqlDialect Dialect { get; } = new FEB.EventSourcing.SqlServer.SqlServerDialect();
    protected override string ConnectionString => fixture.ConnectionString;
    protected override string CreateTablesSql =>
        $"IF OBJECT_ID('{LegacyDbContext.Schema}.legacy_orders') IS NULL CREATE TABLE {LegacyDbContext.Schema}.legacy_orders (Id varchar(100) PRIMARY KEY, Customer varchar(200) NOT NULL); " +
        $"IF OBJECT_ID('{LegacyDbContext.Schema}.projected_orders') IS NULL CREATE TABLE {LegacyDbContext.Schema}.projected_orders (Id varchar(100) PRIMARY KEY, Version int NOT NULL, Customer varchar(200) NOT NULL);";
}
