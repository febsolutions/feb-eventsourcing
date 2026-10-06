using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Snapshots;

public class SnapshotCadenceTests
{
    private static bool ShouldSnapshot(int before, int after, int everyN)
        => SnapshotStore<Customer, string>.ShouldSnapshot(before, after, everyN);

    [Theory]
    // command jumps over the multiple (the old modulo bug would never have snapshotted again)
    [InlineData(8, 12, 10, true)]
    // no multiple crossed
    [InlineData(3, 7, 10, false)]
    // new aggregate, N already reached on the first save
    [InlineData(-1, 1, 2, true)]
    // new aggregate, N not reached yet
    [InlineData(-1, 0, 2, false)]
    // landed exactly on the multiple
    [InlineData(0, 1, 2, true)]
    // right after a snapshot, no new multiple yet
    [InlineData(1, 2, 2, false)]
    // several multiples crossed in one save
    [InlineData(-1, 25, 10, true)]
    public void Crossing_check_detects_due_snapshots(int before, int after, int everyN, bool expected)
        => ShouldSnapshot(before, after, everyN).Should().Be(expected);

    [Fact]
    public void Disabled_cadence_never_snapshots()
        => ShouldSnapshot(-1, 100, 0).Should().BeFalse();

    [Fact]
    public void Unchanged_version_never_snapshots()
        => ShouldSnapshot(5, 5, 2).Should().BeFalse();

    [Fact]
    public void Every_save_snapshots_with_cadence_one()
    {
        ShouldSnapshot(-1, 0, 1).Should().BeTrue();
        ShouldSnapshot(0, 1, 1).Should().BeTrue();
        ShouldSnapshot(7, 8, 1).Should().BeTrue();
    }
}
