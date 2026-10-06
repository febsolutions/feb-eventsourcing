using System.Diagnostics;
using FEB.EventSourcing.Snapshots;

namespace FEB.EventSourcing.Sql;

/// <summary>One latest snapshot per aggregate; payload is the JSON snapshot DTO, optionally compressed.</summary>
public class SqlSnapshotPersistence(
    ISqlDialect dialect,
    SqlEventStoreOptions options,
    ISnapshotSerializer serializer,
    IEventStoreMetrics? mayBeMetrics)
    : ISnapshotPersistence
{
    private const string Source = "sql-snapshots";
    private readonly IEventStoreMetrics _metrics = mayBeMetrics ?? new NoOpEventStoreMetrics();

    public async Task SaveAsync<TAggregate, TId>(object snapshot, Type snapshotType, TId id, int streamVersion, int schemaVersion, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var payload = serializer.Serialize(snapshot, snapshotType, options.Compression);

        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await SqlEventStorePersistence.ExecuteAsync(connection, null, dialect.UpsertSnapshot(options), cancellationToken,
            ("aggregate_type", typeof(TAggregate).Name),
            ("aggregate_id", SqlEventStorePersistence.IdToString(id)),
            ("stream_version", streamVersion),
            ("schema_version", schemaVersion),
            ("payload", payload),
            ("compression", (int)options.Compression),
            ("created_utc", DateTime.UtcNow));

        sw.Stop();
        _metrics.IncrementWrite(snapshotType, Source);
        _metrics.RecordWrite(snapshotType, Source, sw.Elapsed.TotalSeconds);
    }

    public async Task<StoredSnapshot?> LoadAsync<TAggregate, TId>(TId id, Type snapshotType, int expectedSchemaVersion, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT stream_version, schema_version, payload, compression FROM {options.Qualified(options.SnapshotsTable)} " +
            "WHERE aggregate_type = @aggregate_type AND aggregate_id = @aggregate_id";
        SqlEventStorePersistence.AddParameters(cmd,
            ("aggregate_type", typeof(TAggregate).Name),
            ("aggregate_id", SqlEventStorePersistence.IdToString(id)));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        sw.Stop();
        _metrics.IncrementRead(snapshotType, Source);
        _metrics.RecordRead(snapshotType, Source, sw.Elapsed.TotalSeconds);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        if (reader.GetInt32(1) != expectedSchemaVersion)
            return null;

        var payload = (byte[])reader.GetValue(2);
        var compression = (SnapshotCompression)reader.GetInt32(3);
        var snapshot = serializer.Deserialize(payload, snapshotType, compression);

        return new StoredSnapshot(snapshot, reader.GetInt32(0));
    }
}
