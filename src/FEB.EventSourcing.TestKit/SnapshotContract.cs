using System.Reflection;
using System.Text.Json;
using FEB.EventSourcing.Snapshots;

namespace FEB.EventSourcing.TestKit;

/// <summary>
/// Thrown when a snapshot roundtrip loses or corrupts state.
/// </summary>
public sealed class SnapshotContractException(string message) : Exception(message);

/// <summary>
/// Contract test for all <c>[AutoSnapshot]</c> aggregates: fills each instance with
/// deterministic random data, runs CreateSnapshot → Serialize → Deserialize →
/// RestoreFromSnapshot into a fresh instance and deep-compares the snapshots.
/// One test line per application:
/// <code>SnapshotContract.AssertRoundtripsAll(typeof(MyAggregate).Assembly);</code>
/// </summary>
public static class SnapshotContract
{
    /// <summary>
    /// Verifies every snapshot metadata found in the assembly. Throws
    /// <see cref="SnapshotContractException"/> naming the affected property paths
    /// when a roundtrip loses state.
    /// </summary>
    public static void AssertRoundtripsAll(
        Assembly assembly,
        Func<ISnapshotMetadata, bool>? filter = null,
        int seed = 20260814)
    {
        var metas = DiscoverMetadata(assembly)
            .Where(m => filter?.Invoke(m) ?? true)
            .ToList();

        if (metas.Count == 0)
            throw new SnapshotContractException(
                $"No snapshot metadata found in assembly '{assembly.GetName().Name}'. " +
                "Is the source generator referenced and at least one aggregate marked with [AutoSnapshot]?");

        var failures = new List<string>();

        foreach (var meta in metas)
        {
            try
            {
                AssertRoundtrip(meta, seed);
            }
            catch (SnapshotContractException ex)
            {
                failures.Add(ex.Message);
            }
        }

        if (failures.Count > 0)
            throw new SnapshotContractException(string.Join(Environment.NewLine + Environment.NewLine, failures));
    }

    /// <summary>Verifies a single aggregate.</summary>
    public static void AssertRoundtrip(ISnapshotMetadata meta, int seed = 20260814)
    {
        var random = new Random(seed);

        var original = Activator.CreateInstance(meta.AggregateType)
                       ?? throw new SnapshotContractException($"Cannot create instance of {meta.AggregateType.Name}.");

        ObjectFiller.Fill(original, random);

        var snapshotBefore = meta.CreateSnapshot(original);

        // Ensure serializability (System.Text.Json, as in the Redis cache)
        object snapshotAfterSerialization;
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshotBefore, meta.SnapshotType);
            snapshotAfterSerialization = JsonSerializer.Deserialize(json, meta.SnapshotType)
                                         ?? throw new SnapshotContractException(
                                             $"{meta.AggregateType.Name}: snapshot deserialized to null.");
        }
        catch (Exception ex) when (ex is not SnapshotContractException)
        {
            throw new SnapshotContractException(
                $"{meta.AggregateType.Name}: snapshot type {meta.SnapshotType.Name} is not JSON-roundtrip-safe: {ex.Message}");
        }

        var restored = Activator.CreateInstance(meta.AggregateType)!;
        meta.RestoreSnapshot(restored, snapshotAfterSerialization);

        var snapshotAfter = meta.CreateSnapshot(restored);

        var differences = new List<string>();
        DeepComparer.Compare(snapshotBefore, snapshotAfter, meta.AggregateType.Name, differences);

        if (differences.Count > 0)
        {
            var ignored = DescribeIgnoredMembers(meta.AggregateType);
            var ignoredHint = ignored.Count > 0
                ? Environment.NewLine + "Intentionally not snapshotted ([IgnoreSnapshot]): " + string.Join(", ", ignored)
                : string.Empty;

            throw new SnapshotContractException(
                $"{meta.AggregateType.Name}: snapshot roundtrip loses or corrupts state:" + Environment.NewLine +
                string.Join(Environment.NewLine, differences.Select(d => "  - " + d)) +
                ignoredHint);
        }
    }

    /// <summary>
    /// Lists the intentionally not-snapshotted members ([IgnoreSnapshot]) of an
    /// aggregate — for review output, so forgotten state stands out.
    /// </summary>
    public static IReadOnlyList<string> DescribeIgnoredMembers(Type aggregateType)
        => aggregateType
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name == nameof(IgnoreSnapshotAttribute)))
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

    private static IEnumerable<ISnapshotMetadata> DiscoverMetadata(Assembly assembly)
        => assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(ISnapshotMetadata).IsAssignableFrom(t))
            .Select(t => (ISnapshotMetadata)Activator.CreateInstance(t)!);
}
