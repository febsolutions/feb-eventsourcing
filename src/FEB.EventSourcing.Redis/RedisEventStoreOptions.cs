using FEB.EventSourcing.Snapshots;

namespace FEB.EventSourcing.Redis;

public sealed record RedisEventStoreOptions
{
    internal string ConnectionString { get; private set; } = null!;

    internal string CachePrefix { get; private set; } = "eventStore";

    internal SnapshotCompression Compression { get; private set; } = SnapshotCompression.None;

    internal TimeSpan Ttl { get; private set; } = TimeSpan.FromMinutes(30);

    /// <summary>Relative TTL jitter (0..1) to avoid many keys expiring at the same time.</summary>
    internal double TtlJitter { get; private set; } = 0.1;

    public void SetCompression(SnapshotCompression compression) => Compression = compression;

    public void SetConnectionString(string connectionString) => ConnectionString = connectionString;

    public void SetCachePrefix(string cachePrefix) => CachePrefix = cachePrefix;

    public void SetTtl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        Ttl = ttl;
    }

    public void SetTtlJitter(double jitter)
    {
        if (jitter is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(jitter), "Jitter must be in [0, 1).");
        TtlJitter = jitter;
    }
}
