using Testcontainers.Redis;

namespace FEB.EventSourcing.Tests.Infrastructure;

/// <summary>
/// Redis for the integration tests: `ES_TEST_REDIS` (e.g. "redis:6379", externally provided)
/// or a Testcontainers fallback. Tests are isolated by unique key prefixes.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer? _container;
    private string? _connectionString;

    public RedisFixture()
    {
        _connectionString = Environment.GetEnvironmentVariable("ES_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(_connectionString))
            _container = new RedisBuilder("redis:8-alpine").Build();
    }

    public string ConnectionString => _connectionString!;

    public async Task InitializeAsync()
    {
        if (_container is null)
            return;
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public Task DisposeAsync() => _container?.DisposeAsync().AsTask() ?? Task.CompletedTask;
}

[CollectionDefinition("redis")]
public class RedisCollection : ICollectionFixture<RedisFixture>;
