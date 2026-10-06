using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace FEB.EventSourcing.Tests.Infrastructure;

/// <summary>
/// MongoDB for the integration tests - as a single-node replica set, so that the
/// transactional mode (`UseTransactions`) is testable too; the two-step
/// protocol behaves identically on a replica set and standalone. Two modes:
/// - External: `ES_TEST_MONGO` points at a provided server (e.g. "mongodb://mongo:27017")
///   running with `--replSet rs0`; the fixture initiates the replica set idempotently.
/// - Default: without the variable, Testcontainers starts a replica-set container.
/// All connections use directConnection=true - no discovery/hostname issues.
/// Tests are isolated by unique database names.
/// </summary>
public sealed class MongoDbFixture : IAsyncLifetime
{
    private readonly MongoDbContainer? _container;
    private readonly string? _externalBase;
    private string _serverUrl = null!;

    public MongoDbFixture()
    {
        _externalBase = Environment.GetEnvironmentVariable("ES_TEST_MONGO");
        if (string.IsNullOrWhiteSpace(_externalBase))
        {
            _externalBase = null;
            _container = new MongoDbBuilder().WithImage("mongo:8").WithReplicaSet().Build();
        }
    }

    /// <summary>Connection string including the database name (MongoDbContext requires one).</summary>
    public string GetConnectionString(string database)
    {
        var url = new MongoUrlBuilder(_serverUrl) { DatabaseName = database, DirectConnection = true };
        if (!string.IsNullOrEmpty(url.Username))
            url.AuthenticationSource = "admin";
        return url.ToMongoUrl().ToString();
    }

    public async Task InitializeAsync()
    {
        if (_container != null)
        {
            await _container.StartAsync();
            _serverUrl = _container.GetConnectionString();
            return;
        }

        _serverUrl = _externalBase!;
        await EnsureReplicaSetAsync();
    }

    /// <summary>
    /// Initiates the replica set of the externally provided server (started
    /// with `--replSet rs0`) unless already done, and waits for PRIMARY.
    /// </summary>
    private async Task EnsureReplicaSetAsync()
    {
        var url = new MongoUrlBuilder(_serverUrl) { DirectConnection = true }.ToMongoUrl();
        var admin = new MongoClient(url).GetDatabase("admin");

        try
        {
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate", new BsonDocument()));
        }
        catch (MongoCommandException ex) when (ex.CodeName is "AlreadyInitialized")
        {
        }
        catch (MongoCommandException ex) when (ex.CodeName is "NoReplicationEnabled")
        {
            // Server runs without --replSet: two-step tests still work,
            // only the transaction tests would fail - deliberately not hidden.
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
            if (hello.GetValue("isWritablePrimary", false).ToBoolean())
                return;
            await Task.Delay(250);
        }

        throw new TimeoutException("MongoDB replica set did not become PRIMARY within 30 s.");
    }

    public Task DisposeAsync() => _container?.DisposeAsync().AsTask() ?? Task.CompletedTask;
}

[CollectionDefinition("mongo")]
public class MongoCollection : ICollectionFixture<MongoDbFixture>;
