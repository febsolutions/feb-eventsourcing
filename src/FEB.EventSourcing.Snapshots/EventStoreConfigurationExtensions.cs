using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Snapshots;

public static class EventStoreConfigurationExtensions
{
    /// <summary>
    /// Registers the snapshot metadata registry via AppDomain scan (fallback).
    /// Prefer the overload taking explicit metadata.
    /// </summary>
    public static IEventSourcingBuilder UseSnapshots(this IEventSourcingBuilder builder)
    {
        builder.RegisterInfrastructure(services =>
        {
            builder.Services.AddSingleton<ISnapshotMetadataRegistry>(sp => new SnapshotMetadataRegistry(
                AppDomain.CurrentDomain.GetAssemblies()));
        });

        return builder;
    }

    /// <summary>
    /// Preferred: explicit metadata from the generated
    /// <c>SnapshotMetadataModule_&lt;Assembly&gt;.CreateAll()</c> modules — deterministic,
    /// no AppDomain scan.
    /// </summary>
    public static IEventSourcingBuilder UseSnapshots(
        this IEventSourcingBuilder builder,
        IEnumerable<ISnapshotMetadata> metadata)
    {
        builder.RegisterInfrastructure(services =>
        {
            var registry = new SnapshotMetadataRegistry(metadata);
            builder.Services.AddSingleton<ISnapshotMetadataRegistry>(registry);
        });

        return builder;
    }

    /// <summary>
    /// Takes snapshot writes off the command path: jobs go to a bounded queue drained
    /// by a hosted service. Without this call, snapshot stores write inline (default).
    /// </summary>
    public static IEventSourcingBuilder UseBackgroundSnapshotWrites(
        this IEventSourcingBuilder builder,
        int queueCapacity = 1024)
    {
        builder.RegisterInfrastructure(services =>
        {
            services.AddSingleton(new ChannelSnapshotWriteQueue(queueCapacity));
            services.AddSingleton<ISnapshotWriteQueue>(sp => sp.GetRequiredService<ChannelSnapshotWriteQueue>());
            services.AddHostedService<SnapshotWriteWorker>();
        });

        return builder;
    }
}
