using System.Reflection;
using FEB.EventSourcing.MongoDb;
using FEB.EventSourcing.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

// ReSharper disable once CheckNamespace
namespace FEB.EventSourcing;

public static class MongoDbEventStoreConfigurationExtensions
{
    public static IEventSourcingBuilder UseMongoDb<TMongoDbContext>(
        this IEventSourcingBuilder builder,
        Action<MongoEventStoreOptions>? configure = null)
        where TMongoDbContext : class, IMongoDbContext
    {
        var options = new MongoEventStoreOptions();

        configure?.Invoke(options);

        UseMongoDbCore(
            builder,
            options,
            services =>
            {
                services.AddTransient<IMongoDbContext, TMongoDbContext>();
            });

        return builder;
    }

    public static IEventSourcingBuilder UseMongoDb(
        this IEventSourcingBuilder builder,
        string connectionString,
        Action<MongoEventStoreOptions>? configure = null)
    {
        var options = new MongoEventStoreOptions();
        options.SetConnectionString(connectionString);

        configure?.Invoke(options);

        UseMongoDbCore(
            builder,
            options,
            services =>
            {
                services.AddTransient<IMongoDbContext>(
                    _ => new MongoDbContext(connectionString));
            });

        return builder;
    }

    
    private static void UseMongoDbCore(
        IEventSourcingBuilder builder,
        MongoEventStoreOptions options,
        Action<IServiceCollection> registerMongoDbContext)
    {
        if (builder.IsPersistenceRegistered)
            throw new InvalidOperationException("Persistence already registered.");
        
        builder.RegisterInfrastructure(services =>
        {
            services.AddSingleton(options);

            services.AddSingleton(
                new EventStoreCollectionNameBuilder(options.EventStorePrefix));

            // How the context is registered depends on the overload
            registerMongoDbContext(services);

            services.AddTransient<IEventStorePersistence, MongoDbEventStorePersistence>();
            services.AddTransient<ISnapshotPersistence, MongoDbSnapshotPersistence>();

            services.AddSingleton<MongoDbSnapshotSerializer>();

            if (options.SnapshotsEnabled)
                builder.EventStoreChainBuilder.AddDecorator(new SnapshotStoreRegistration());

            // MongoDB Serializer (global, idempotent)
            var objectSerializer = new ObjectSerializer(ObjectSerializer.AllAllowedTypes);
            BsonSerializer.TryRegisterSerializer(objectSerializer);
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

            // Event class maps from the explicit application assemblies (deterministic,
            // no AppDomain scan) — IgnoreExtraElements keeps removed event properties readable.
            RegisterEventsInMongoDb(builder.ApplicationAssemblies);

            // Startup: create the indexes the store relies on for all registered aggregates.
            if (options.EnsureIndexesOnStartup)
                services.AddHostedService<MongoDbIndexInitializer>();

            builder.SetPersistenceRegistered();
        });
    }

    public static IEventSourcingBuilder UseMongoOutbox(
        this IEventSourcingBuilder builder,
        Action<MongoOutboxOptions>? configure = null)
    {
        builder.RegisterInfrastructure(services =>
        {
            var options = new MongoOutboxOptions();

            configure?.Invoke(options);
            
            services.AddSingleton(options);
            builder.Services.AddScoped<IOutbox, Outbox>();
            builder.Services.AddTransient<IOutboxPersistence, MongoDbOutboxPersistence>();
        });
       
        return builder;
    }
    
    private static void RegisterEventsInMongoDb(IEnumerable<System.Reflection.Assembly> assemblies)
    {
        var eventInterface = typeof(IEvent);
        foreach (var assembly in assemblies)
        {
            var types = assembly
                .GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && eventInterface.IsAssignableFrom(t));

            foreach (var type in types)
            {
                if (BsonClassMap.IsClassMapRegistered(type)) 
                    continue;
                var map = new BsonClassMap(type);
                map.AutoMap();
                map.SetIgnoreExtraElements(true);

                // [EventName] becomes the BSON discriminator so the class can move. The
                // CLR short name (the default before the attribute) and all aliases stay
                // readable as well - existing data needs no migration.
                var configuredName = EventTypeNames.GetConfiguredName(type);
                if (configuredName != null)
                    map.SetDiscriminator(configuredName);

                BsonClassMap.RegisterClassMap(map);

                if (configuredName != null)
                {
                    BsonSerializer.RegisterDiscriminator(type, type.Name);

                    foreach (var alias in type.GetCustomAttribute<EventNameAttribute>(false)!.Aliases)
                        BsonSerializer.RegisterDiscriminator(type, alias);
                }
            }
        }
    }
}