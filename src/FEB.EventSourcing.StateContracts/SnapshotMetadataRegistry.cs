using System.Reflection;

namespace FEB.EventSourcing.Snapshots;

public sealed class SnapshotMetadataRegistry : ISnapshotMetadataRegistry
{
    private readonly Dictionary<Type, ISnapshotMetadata> _cache;

    /// <summary>
    /// Preferred: explicit metadata, e.g. from the generated
    /// <c>SnapshotMetadataModule_&lt;Assembly&gt;.CreateAll()</c> — deterministic, no reflection scan.
    /// </summary>
    public SnapshotMetadataRegistry(IEnumerable<ISnapshotMetadata> metadata)
        => _cache = metadata.ToDictionary(m => m.AggregateType);

    /// <summary>
    /// Fallback via assembly scan. Only finds metadata in the given assemblies —
    /// prefer the explicit overload.
    /// </summary>
    public SnapshotMetadataRegistry(params Assembly[] assemblies)
        => _cache = BuildCache(assemblies);

    public ISnapshotMetadata? GetForAggregate(Type aggregateType)
        => _cache.TryGetValue(aggregateType, out var meta)
            ? meta
            : null;

    private static Dictionary<Type, ISnapshotMetadata> BuildCache(Assembly[] assemblies)
        => assemblies
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && typeof(ISnapshotMetadata).IsAssignableFrom(t))
            .Select(t => (ISnapshotMetadata)Activator.CreateInstance(t)!)
            .ToDictionary(m => m.AggregateType);
}
