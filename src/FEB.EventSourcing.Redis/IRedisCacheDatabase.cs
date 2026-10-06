using FEB.EventSourcing.Snapshots;

namespace FEB.EventSourcing.Redis;

/// <summary>
/// A cached aggregate state: the serialized snapshot DTO plus the metadata stored
/// as individual Redis hash fields (no envelope JSON).
/// </summary>
public sealed record CachedAggregateState(
    byte[] Payload,
    int StreamVersion,
    int SchemaVersion,
    SnapshotCompression Compression,
    DateTime CreatedUtc);

/// <summary>
/// Narrow Redis abstraction for the aggregate cache. One key per aggregate as a hash
/// (fields v/sv/p/c/t); writes are version-guarded via compare-and-set.
/// </summary>
public interface IRedisCacheDatabase
{
    Task<CachedAggregateState?> GetAsync(string key);

    /// <summary>
    /// Writes only when the new stream version is higher than the stored one
    /// (Lua CAS). Returns false when a newer entry won.
    /// </summary>
    Task<bool> TrySetAsync(string key, CachedAggregateState state, TimeSpan ttl);

    Task<bool> ExistsAsync(string key);

    Task DeleteAsync(string key);
}
