using FEB.EventSourcing.Redis;
using FEB.EventSourcing.Snapshots;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace FEB.EventSourcing;

public static class RedisEventStoreConfigurationExtensions
{
    public static IEventSourcingBuilder UseRedis(
        this IEventSourcingBuilder builder,
        string connectionString,
        Action<RedisEventStoreOptions>? configure = null)
    {
        builder.RegisterInfrastructure(services =>
        {
            var options = new RedisEventStoreOptions();
            options.SetConnectionString(connectionString);

            configure?.Invoke(options);

            if (!builder.IsPersistenceRegistered)
                throw new InvalidOperationException("Redis Event Store requires a persistance provider to be registered first!");

            services.AddSingleton(options);
            services.AddSingleton<IRedisCacheDatabase>(_ => new RedisCacheDatabase(options));
            services.AddSingleton<ISnapshotSerializer, RedisSnapshotSerializer>();

            builder.EventStoreChainBuilder.AddDecorator(new RedisRegistration(options));
        });

        return builder;
    }
}
