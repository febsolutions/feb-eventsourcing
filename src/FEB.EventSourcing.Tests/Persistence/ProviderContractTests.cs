using FEB.EventSourcing.MongoDb;
using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Sql;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.TestDomain;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Persistence;

// One concrete class per provider — every contract test runs once per provider.

[Collection("mongo")]
public sealed class MongoDbContractTests(MongoDbFixture fixture) : PersistenceContractTests
{
    protected override ServiceProvider BuildHost(string suffix, bool withOutbox = false)
    {
        var services = new ServiceCollection();
        services.AddEventSourcing(es =>
        {
            es.UseMongoDb(fixture.GetConnectionString($"contract_{suffix}"), o => o.EnableSnapshots());
            es.UseSnapshots();
            if (withOutbox)
            {
                es.UseMongoOutbox(o => o.SetMaxAttempts(5));
                es.RegisterContractSubscribers();
            }
            es.RegisterAggregates();
        }, typeof(TestHost).Assembly);
        return services.BuildServiceProvider();
    }

    protected override Task InitializeStorageAsync(ServiceProvider sp)
        => sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<MongoDbIndexInitializer>().Single().EnsureAllAsync();
}

[Collection("postgres")]
public sealed class PostgresContractTests(PostgresFixture fixture) : PersistenceContractTests
{
    protected override ServiceProvider BuildHost(string suffix, bool withOutbox = false)
    {
        var services = new ServiceCollection();
        services.AddEventSourcing(es =>
        {
            es.UsePostgres(fixture.ConnectionString, o =>
            {
                o.EnableSnapshots();
                o.SetSchema($"c_{suffix}");
            });
            es.UseSnapshots();
            if (withOutbox)
            {
                es.UseSqlOutbox(o => o.SetMaxAttempts(5));
                es.RegisterContractSubscribers();
            }
            es.RegisterAggregates();
        }, typeof(TestHost).Assembly);
        return services.BuildServiceProvider();
    }

    protected override Task InitializeStorageAsync(ServiceProvider sp)
        => sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<SqlSchemaInitializer>().Single().EnsureAsync();
}

[Collection("sqlserver")]
public sealed class SqlServerContractTests(SqlServerFixture fixture) : PersistenceContractTests
{
    protected override ServiceProvider BuildHost(string suffix, bool withOutbox = false)
    {
        var services = new ServiceCollection();
        services.AddEventSourcing(es =>
        {
            es.UseSqlServer(fixture.ConnectionString, o =>
            {
                o.EnableSnapshots();
                o.SetSchema($"c_{suffix}");
            });
            es.UseSnapshots();
            if (withOutbox)
            {
                es.UseSqlOutbox(o => o.SetMaxAttempts(5));
                es.RegisterContractSubscribers();
            }
            es.RegisterAggregates();
        }, typeof(TestHost).Assembly);
        return services.BuildServiceProvider();
    }

    protected override Task InitializeStorageAsync(ServiceProvider sp)
        => sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<SqlSchemaInitializer>().Single().EnsureAsync();
}

file static class ContractWiring
{
    public static void RegisterAggregates(this IEventSourcingBuilder es)
    {
        es.Aggregate<Order, string>(a => a.Factory(Order.CreateNew));
        es.Aggregate<Customer, string>(a =>
        {
            a.Factory(Customer.CreateNew);
            a.Snapshots(s => s.EveryNEvents(2));
        });
    }

    public static void RegisterContractSubscribers(this IEventSourcingBuilder es)
    {
        es.UseDefaultOutboxSubscriber();
        es.UseOutboxSubscriber<PersistenceContractTests.ContractRecordingSubscriber>();
        es.UseOutboxSubscriber<PersistenceContractTests.ContractFlakySubscriber>();
        es.UseOutboxWorker(o => o.SetPollingInterval(TimeSpan.FromMilliseconds(50)));
    }
}
