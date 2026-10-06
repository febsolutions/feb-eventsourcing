using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Sql;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace FEB.EventSourcing;

/// <summary>
/// Dialect-neutral registration used by <c>UsePostgres(...)</c> and <c>UseSqlServer(...)</c>.
/// Call those from application code; call this only when providing your own dialect.
/// </summary>
public static class SqlEventStoreConfigurationExtensions
{
    public static IEventSourcingBuilder UseSql(
        this IEventSourcingBuilder builder,
        ISqlDialect dialect,
        string connectionString,
        Action<SqlEventStoreOptions>? configure = null)
    {
        if (builder.IsPersistenceRegistered)
            throw new InvalidOperationException("Persistence already registered.");

        var options = new SqlEventStoreOptions();
        options.SetConnectionString(connectionString);
        configure?.Invoke(options);

        builder.RegisterInfrastructure(services =>
        {
            services.AddSingleton(options);
            services.AddSingleton(dialect);

            services.AddTransient<IEventStorePersistence, SqlEventStorePersistence>();
            services.AddSingleton<ISnapshotSerializer, SqlSnapshotSerializer>();
            services.AddTransient<ISnapshotPersistence>(sp => new SqlSnapshotPersistence(
                dialect, options,
                sp.GetRequiredService<ISnapshotSerializer>(),
                sp.GetService<IEventStoreMetrics>()));

            if (options.SnapshotsEnabled)
                builder.EventStoreChainBuilder.AddDecorator(new SnapshotStoreRegistration());

            if (options.EnsureSchemaOnStartup)
                services.AddHostedService<SqlSchemaInitializer>();

            builder.SetPersistenceRegistered();
        });

        return builder;
    }

    /// <summary>Relational outbox (per-subscriber delivery tracking, SKIP LOCKED / READPAST leasing).</summary>
    public static IEventSourcingBuilder UseSqlOutbox(
        this IEventSourcingBuilder builder,
        Action<SqlOutboxOptions>? configure = null)
    {
        builder.RegisterInfrastructure(services =>
        {
            var options = new SqlOutboxOptions();
            configure?.Invoke(options);

            services.AddSingleton(options);
            services.AddScoped<IOutbox, Outbox>();
            services.AddTransient<IOutboxPersistence, SqlOutboxPersistence>();
        });

        return builder;
    }
}
