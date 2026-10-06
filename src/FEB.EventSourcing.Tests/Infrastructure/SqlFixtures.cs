using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace FEB.EventSourcing.Tests.Infrastructure;

/// <summary>
/// PostgreSQL: `ES_TEST_POSTGRES` (full Npgsql connection string of an externally provided server)
/// or a Testcontainers fallback. Tests are isolated by unique schemas.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _container;
    private string? _connectionString;

    public PostgresFixture()
    {
        _connectionString = Environment.GetEnvironmentVariable("ES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(_connectionString))
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
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

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>
/// SQL Server: `ES_TEST_SQLSERVER` (full connection string incl.
/// TrustServerCertificate=true, externally provided server) or a Testcontainers fallback.
/// Tests are isolated by unique schemas.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer? _container;
    private string? _connectionString;

    public SqlServerFixture()
    {
        _connectionString = Environment.GetEnvironmentVariable("ES_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(_connectionString))
            _container = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
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

[CollectionDefinition("sqlserver")]
public class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
