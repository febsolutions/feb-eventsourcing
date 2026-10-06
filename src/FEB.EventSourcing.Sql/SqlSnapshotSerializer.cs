using System.Text.Json;
using FEB.EventSourcing.Snapshots;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace FEB.EventSourcing.Sql;

/// <summary>JSON (System.Text.Json) snapshot serializer with optional LZ4/Zstd compression.</summary>
public sealed class SqlSnapshotSerializer : ISnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public byte[] Serialize<T>(T snapshot, Type type, SnapshotCompression compression)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var raw = JsonSerializer.SerializeToUtf8Bytes(snapshot, type, JsonOptions);

        return compression switch
        {
            SnapshotCompression.None => raw,
            SnapshotCompression.Lz4 => LZ4Pickler.Pickle(raw),
            SnapshotCompression.Zstd => Zstd(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(compression))
        };
    }

    public object Deserialize(byte[] payload, Type snapshotType, SnapshotCompression compression)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var raw = compression switch
        {
            SnapshotCompression.None => payload,
            SnapshotCompression.Lz4 => LZ4Pickler.Unpickle(payload),
            SnapshotCompression.Zstd => Unzstd(payload),
            _ => throw new ArgumentOutOfRangeException(nameof(compression))
        };

        return JsonSerializer.Deserialize(raw, snapshotType, JsonOptions)
               ?? throw new InvalidOperationException($"Failed to deserialize snapshot of type {snapshotType.FullName}");
    }

    private static byte[] Zstd(byte[] raw)
    {
        using var c = new Compressor();
        return c.Wrap(raw).ToArray();
    }

    private static byte[] Unzstd(byte[] data)
    {
        using var d = new Decompressor();
        return d.Unwrap(data).ToArray();
    }
}
