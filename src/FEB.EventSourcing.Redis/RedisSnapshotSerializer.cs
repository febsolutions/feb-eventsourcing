using System.Text;
using System.Text.Json;
using FEB.EventSourcing.Snapshots;
using K4os.Compression.LZ4;
using ZstdNet;

namespace FEB.EventSourcing.Redis;

public class RedisSnapshotSerializer : ISnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        IncludeFields = false
    };
    
    public byte[] Serialize<T>(T snapshot, Type type, SnapshotCompression compression)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));

        // 1) JSON → UTF8
        var json = JsonSerializer.Serialize(snapshot, type, JsonOptions);
        var raw = Encoding.UTF8.GetBytes(json);

        return compression switch
        {
            SnapshotCompression.None => raw,
            SnapshotCompression.Lz4  => LZ4Pickler.Pickle(raw),
            SnapshotCompression.Zstd => CompressZstd(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(compression))
        };
    }

    public object Deserialize(byte[] payload, Type snapshotType, SnapshotCompression compression)
    {
        if (payload is null)
            throw new ArgumentNullException(nameof(payload));

        var raw = compression switch
        {
            SnapshotCompression.None => payload,
            SnapshotCompression.Lz4  => LZ4Pickler.Unpickle(payload),
            SnapshotCompression.Zstd => DecompressZstd(payload),
            _ => throw new ArgumentOutOfRangeException(nameof(compression))
        };

        var json = Encoding.UTF8.GetString(raw);

        return JsonSerializer.Deserialize(json, snapshotType, JsonOptions)
               ?? throw new InvalidOperationException(
                   $"Failed to deserialize snapshot of type {snapshotType.FullName}");

    }

    // --- Zstd helpers ---

    private static byte[] CompressZstd(byte[] raw)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(raw);
    }

    private static byte[] DecompressZstd(byte[] compressed)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(compressed);
    }
}