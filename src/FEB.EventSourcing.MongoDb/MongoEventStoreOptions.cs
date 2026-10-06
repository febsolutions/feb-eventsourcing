using FEB.EventSourcing.Snapshots;

namespace FEB.EventSourcing.MongoDb;

public sealed class MongoEventStoreOptions
{
    internal bool SnapshotsEnabled { get; private set; }
    
    internal string EventStorePrefix { get; private set; } = "EventStore";
    
    internal string ConnectionString { get; private set; } = null!;

    internal bool EnsureIndexesOnStartup { get; private set; } = true;

    internal bool TransactionsEnabled { get; private set; }

    public SnapshotCompression Compression { get; set; } = SnapshotCompression.None;

    /// <summary>Disable the startup index initializer (e.g. when indexes are managed externally).</summary>
    public void DisableIndexInitialization() => EnsureIndexesOnStartup = false;

    /// <summary>
    /// Runs the version check and the event insert of every append in one
    /// multi-document transaction: no intermediate state can ever be observed or
    /// left behind, so the self-heal machinery of the default two-step protocol
    /// becomes a pure safety net. Requires a MongoDB replica set — a single-node
    /// replica set is sufficient; on a standalone server appends will fail.
    /// </summary>
    public void UseTransactions() => TransactionsEnabled = true;

    public void SetCompression(SnapshotCompression compression) => Compression = compression;

    public void EnableSnapshots()
        => SnapshotsEnabled = true;
    
    public void SetEventStoreCollectionPrefix(string prefix) 
        => EventStorePrefix = prefix;
    
    public void SetConnectionString(string connectionString)
        => ConnectionString = connectionString;
}