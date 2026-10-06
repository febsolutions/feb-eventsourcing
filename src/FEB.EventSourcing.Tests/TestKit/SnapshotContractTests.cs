using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.TestKit;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.TestKit;

public class SnapshotContractTests
{
    [Fact]
    public void All_test_domain_aggregates_roundtrip_completely()
    {
        // The one contract test every consuming application writes.
        // (Filter: exclude the deliberately broken metadata from this test file.)
        SnapshotContract.AssertRoundtripsAll(
            typeof(Customer).Assembly,
            m => m.AggregateType != typeof(LossyThing));
    }

    [Fact]
    public void Lossy_restore_is_detected_with_property_path()
    {
        var act = () => SnapshotContract.AssertRoundtrip(new LossyMeta());

        act.Should().Throw<SnapshotContractException>()
            .Which.Message.Should().Contain("LossyThing.B", "the path of the lost state must be named");
    }

    [Fact]
    public void Ignored_members_are_reported_for_review()
    {
        SnapshotContract.DescribeIgnoredMembers(typeof(Customer))
            .Should().Contain(nameof(Customer.TransientNote));
    }

    [Fact]
    public void Assembly_without_metadata_gives_a_helpful_error()
    {
        var act = () => SnapshotContract.AssertRoundtripsAll(typeof(SnapshotContract).Assembly);

        act.Should().Throw<SnapshotContractException>()
            .Which.Message.Should().Contain("[AutoSnapshot]");
    }

    // --- deliberately broken example: restore loses property B ---

    public sealed class LossyThing
    {
        public string A { get; set; } = string.Empty;
        public string B { get; set; } = string.Empty;
    }

    public sealed class LossyThingSnapshot
    {
        public string A { get; set; } = string.Empty;
        public string B { get; set; } = string.Empty;
    }

    private sealed class LossyMeta : ISnapshotMetadata
    {
        public Type AggregateType => typeof(LossyThing);
        public Type SnapshotType => typeof(LossyThingSnapshot);
        public int Version => 1;

        public object CreateSnapshot(object aggregate)
        {
            var thing = (LossyThing)aggregate;
            return new LossyThingSnapshot { A = thing.A, B = thing.B };
        }

        public void RestoreSnapshot(object aggregate, object snapshot)
        {
            // B is "forgotten" - exactly what the contract test must find
            ((LossyThing)aggregate).A = ((LossyThingSnapshot)snapshot).A;
        }
    }
}
