using FEB.EventSourcing.Snapshots;
using StackExchange.Redis;

namespace FEB.EventSourcing.Redis;

public sealed class RedisCacheDatabase : IRedisCacheDatabase
{
    // Only set when the new stream version is higher than the stored one.
    private const string CasScript = """
        local cur = redis.call('HGET', KEYS[1], 'v')
        if cur and tonumber(cur) >= tonumber(ARGV[1]) then return 0 end
        redis.call('HSET', KEYS[1], 'v', ARGV[1], 'sv', ARGV[2], 'p', ARGV[3], 'c', ARGV[4], 't', ARGV[5])
        redis.call('PEXPIRE', KEYS[1], ARGV[6])
        return 1
        """;

    private readonly IDatabase _db;
    private readonly string _prefix;

    public RedisCacheDatabase(RedisEventStoreOptions options)
        : this(ConnectionMultiplexer.Connect(options.ConnectionString), options.CachePrefix)
    {
    }

    public RedisCacheDatabase(IConnectionMultiplexer connection, string prefix)
    {
        _db = connection.GetDatabase();
        _prefix = prefix;
    }

    private string BuildKey(string key) => $"{_prefix}:{key}";

    public async Task<CachedAggregateState?> GetAsync(string key)
    {
        var values = await _db.HashGetAsync(BuildKey(key), ["v", "sv", "p", "c", "t"]);

        if (values[2].IsNull)
            return null;

        return new CachedAggregateState(
            Payload: (byte[])values[2]!,
            StreamVersion: (int)values[0],
            SchemaVersion: (int)values[1],
            Compression: (SnapshotCompression)(int)values[3],
            CreatedUtc: DateTimeOffset.FromUnixTimeMilliseconds((long)values[4]).UtcDateTime);
    }

    public async Task<bool> TrySetAsync(string key, CachedAggregateState state, TimeSpan ttl)
    {
        var result = await _db.ScriptEvaluateAsync(
            CasScript,
            [BuildKey(key)],
            [
                state.StreamVersion,
                state.SchemaVersion,
                state.Payload,
                (int)state.Compression,
                new DateTimeOffset(state.CreatedUtc).ToUnixTimeMilliseconds(),
                (long)ttl.TotalMilliseconds
            ]);

        return (int)result == 1;
    }

    public Task<bool> ExistsAsync(string key) => _db.KeyExistsAsync(BuildKey(key));

    public Task DeleteAsync(string key) => _db.KeyDeleteAsync(BuildKey(key));
}
