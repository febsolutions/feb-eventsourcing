using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Snapshots.Generated;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Snapshots;

public class SnapshotMetadataModuleTests
{
    [Fact]
    public void Generated_module_lists_all_metadata_of_this_assembly()
    {
        var metas = SnapshotMetadataModule_FEB_EventSourcing_Tests.CreateAll();

        metas.Should().Contain(m => m.AggregateType == typeof(Customer));
        metas.Should().Contain(m => m.AggregateType == typeof(Supplier));
        metas.Select(m => m.AggregateType).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Registry_can_be_built_from_generated_module_without_assembly_scan()
    {
        var registry = new SnapshotMetadataRegistry(SnapshotMetadataModule_FEB_EventSourcing_Tests.CreateAll());

        registry.GetForAggregate(typeof(Customer)).Should().NotBeNull();
        registry.GetForAggregate(typeof(Supplier)).Should().NotBeNull();
        registry.GetForAggregate(typeof(Order)).Should().BeNull();
    }
}
