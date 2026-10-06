using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Snapshots;

/// <summary>
/// Tests against the code produced by the source generator (the generator runs as an analyzer
/// on this test project, see the csproj).
/// </summary>
public class SnapshotRoundtripTests
{
    private static Customer BuildCustomer()
    {
        var customer = Customer.CreateNew("cust-1");
        customer.Register("ACME GmbH", CustomerKind.Company, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        customer.Relocate("Hauptstraße 1", "Berlin");
        customer.AddContact("Alex", "alex@acme.test");
        customer.AddContact("Kim", "kim@acme.test");
        customer.CreditLimit = 5000m;
        customer.Category = "A-customer";
        customer.Tags = ["vip", "b2b"];
        customer.TransientNote = "do not persist";
        customer.Commit(3);
        return customer;
    }

    [Fact]
    public void CreateSnapshot_captures_all_mapped_properties()
    {
        var snapshot = BuildCustomer().CreateSnapshot();

        snapshot.Name.Should().Be("ACME GmbH");
        snapshot.Kind.Should().Be(CustomerKind.Company);
        snapshot.CreditLimit.Should().Be(5000m);
        snapshot.Address.Should().NotBeNull();
        snapshot.Address!.City.Should().Be("Berlin");
        snapshot.Contacts.Should().HaveCount(2);
        snapshot.Tags.Should().BeEquivalentTo("vip", "b2b");
        snapshot.Category.Should().Be("A-customer", "state from in-source base classes must be included");
    }

    [Fact]
    public void Primitive_list_roundtrips_as_copy()
    {
        var original = BuildCustomer();
        var snapshot = original.CreateSnapshot();

        var restored = Customer.CreateNew("cust-1");
        restored.RestoreFromSnapshot(snapshot);

        restored.Tags.Should().BeEquivalentTo("vip", "b2b");
        restored.Tags.Should().NotBeSameAs(original.Tags);
    }

    [Fact]
    public void Shared_nested_type_is_usable_from_second_aggregate()
    {
        var supplier = Supplier.CreateNew("sup-1");
        supplier.Name = "Liefer GmbH";
        supplier.Address = new Address { Street = "Weg 2", City = "Hamburg" };

        var snapshot = supplier.CreateSnapshot();
        var restored = Supplier.CreateNew("sup-1");
        restored.RestoreFromSnapshot(snapshot);

        restored.Address!.City.Should().Be("Hamburg");
    }

    [Fact]
    public void Roundtrip_restores_an_equivalent_aggregate()
    {
        var original = BuildCustomer();

        var snapshot = original.CreateSnapshot();
        var restored = Customer.CreateNew("cust-1");
        restored.RestoreFromSnapshot(snapshot);

        restored.Should().BeEquivalentTo(original, o => o
            .Excluding(c => c.TransientNote)
            .Excluding(c => c.Version));
        restored.TransientNote.Should().BeNull("[IgnoreSnapshot] properties must not be carried over");
    }

    [Fact]
    public void Roundtrip_does_not_alias_nested_objects_or_lists()
    {
        var original = BuildCustomer();
        var snapshot = original.CreateSnapshot();

        var restored = Customer.CreateNew("cust-1");
        restored.RestoreFromSnapshot(snapshot);

        restored.Address.Should().NotBeSameAs(original.Address);
        restored.Contacts.Should().NotBeSameAs(original.Contacts);
        restored.Contacts[0].Should().NotBeSameAs(original.Contacts[0]);
    }

    [Fact]
    public void Null_nested_object_roundtrips_as_null()
    {
        var customer = Customer.CreateNew("cust-1");
        customer.Register("Solo", CustomerKind.Person, DateTime.UtcNow);

        var snapshot = customer.CreateSnapshot();
        var restored = Customer.CreateNew("cust-1");
        restored.RestoreFromSnapshot(snapshot);

        restored.Address.Should().BeNull();
        restored.Contacts.Should().BeEmpty();
    }

    [Fact]
    public void Registry_finds_generated_metadata_for_aggregate()
    {
        var registry = new SnapshotMetadataRegistry(typeof(Customer).Assembly);

        var meta = registry.GetForAggregate(typeof(Customer));

        meta.Should().NotBeNull();
        meta!.AggregateType.Should().Be<Customer>();
        meta.SnapshotType.Should().Be<CustomerSnapshot>();
        meta.Version.Should().NotBe(0);
    }

    [Fact]
    public void Metadata_roundtrip_via_object_api_works()
    {
        var original = BuildCustomer();
        var registry = new SnapshotMetadataRegistry(typeof(Customer).Assembly);
        var meta = registry.GetForAggregate(typeof(Customer))!;

        var snapshot = meta.CreateSnapshot(original);
        var restored = Customer.CreateNew("cust-1");
        meta.RestoreSnapshot(restored, snapshot);

        restored.Name.Should().Be(original.Name);
        restored.Contacts.Should().HaveCount(2);
    }

    [Fact]
    public void Registry_returns_null_for_aggregates_without_snapshot()
    {
        var registry = new SnapshotMetadataRegistry(typeof(Customer).Assembly);

        registry.GetForAggregate(typeof(Order)).Should().BeNull();
    }
}
