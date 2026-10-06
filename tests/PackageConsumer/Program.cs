using FEB.EventSourcing;
using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Snapshots.Generated;

// SnapshotMetadataModule_PackageConsumer only exists if the source generator ran in this
// consumer — i.e. if it was packed into FEB.EventSourcing.StateContracts and flowed
// through the package dependencies.
var metadata = SnapshotMetadataModule_PackageConsumer.CreateAll().ToList();
Console.WriteLine($"Generated snapshot metadata: {metadata.Count}");

const int expected = 2;   // one aggregate in a namespace, one in the global namespace
if (metadata.Count != expected)
{
    Console.Error.WriteLine($"Expected {expected} generated snapshot metadata entries.");
    return 1;
}

Console.WriteLine("Package smoke test passed.");
return 0;

// The global namespace is a valid place for an aggregate (top-level programs, samples).
[AutoSnapshot]
public partial class GlobalCounter : AggregateRoot<GlobalCounter, string>
{
    public int Value { get; set; }
    public override void EnsureHasId() { }
}

namespace PackageConsumer.Domain
{
    [AutoSnapshot]
    public partial class Counter : AggregateRoot<Counter, string>
    {
        public int Value { get; set; }
        public List<string> Tags { get; set; } = [];
        public override void EnsureHasId() { }
    }
}
