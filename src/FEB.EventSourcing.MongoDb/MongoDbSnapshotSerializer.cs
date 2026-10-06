using FEB.EventSourcing.Snapshots;
using K4os.Compression.LZ4;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using ZstdSharp;

namespace FEB.EventSourcing.MongoDb;

public class MongoDbSnapshotSerializer : ISnapshotSerializer
{
    public byte[] Serialize<T>(T snapshot, Type type, SnapshotCompression compression)
    {
        var raw = SerializeToBson(snapshot, type);

        return compression switch
        {
            SnapshotCompression.None => raw,
            SnapshotCompression.Lz4 => LZ4Pickler.Pickle(raw, LZ4Level.L00_FAST),
            SnapshotCompression.Zstd => CompressZstd(raw),

            _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null)
        };
    }

    public object Deserialize(byte[] payload, Type type, SnapshotCompression compression)
    {
        var raw = compression switch
        {
            SnapshotCompression.None => payload,
            SnapshotCompression.Lz4 => LZ4Pickler.Unpickle(payload),
            SnapshotCompression.Zstd => DecompressZstd(payload),
            _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null)
        };

        return DeserializeFromBson(raw, type);
    }

    private static byte[] CompressZstd(byte[] raw)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(raw).ToArray();
    }

    private static byte[] DecompressZstd(byte[] payload)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(payload).ToArray();
    }

    private static byte[] SerializeToBson<T>(T snapshot, Type snapshotType)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));

        using var ms = new MemoryStream();
        using (var writer = new BsonBinaryWriter(ms))
        {
            BsonSerializer.Serialize(writer, snapshotType, snapshot);
        }

        return ms.ToArray();
    }
    
    private static object DeserializeFromBson(byte[] data, Type snapshotType)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (snapshotType is null)
            throw new ArgumentNullException(nameof(snapshotType));

        using var ms = new MemoryStream(data);
        using var reader = new BsonBinaryReader(ms);

        return BsonSerializer.Deserialize(reader, snapshotType);
    }
}