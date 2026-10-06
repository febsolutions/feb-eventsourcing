using FEB.EventSourcing.MongoDb;
using FEB.EventSourcing.Redis;
using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Tests.TestDomain;
using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Snapshots;

public class SnapshotSerializerTests
{
    private static CustomerSnapshot BuildSnapshot()
    {
        var customer = Customer.CreateNew("cust-1");
        customer.Register("ACME GmbH", CustomerKind.Company, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        customer.Relocate("Hauptstraße 1", "Berlin");
        customer.AddContact("Alex", "alex@acme.test");
        customer.Tags = ["vip"];
        return customer.CreateSnapshot();
    }

    [Theory]
    [InlineData(SnapshotCompression.None)]
    [InlineData(SnapshotCompression.Lz4)]
    [InlineData(SnapshotCompression.Zstd)]
    public void Mongo_bson_serializer_roundtrips_with_all_compressions(SnapshotCompression compression)
    {
        var serializer = new MongoDbSnapshotSerializer();
        var snapshot = BuildSnapshot();

        var payload = serializer.Serialize(snapshot, typeof(CustomerSnapshot), compression);
        var restored = (CustomerSnapshot)serializer.Deserialize(payload, typeof(CustomerSnapshot), compression);

        restored.Should().BeEquivalentTo(snapshot);
    }

    [Theory]
    [InlineData(SnapshotCompression.None)]
    [InlineData(SnapshotCompression.Lz4)]
    public void Redis_json_serializer_roundtrips_with_all_compressions(SnapshotCompression compression)
    {
        var serializer = new RedisSnapshotSerializer();
        var snapshot = BuildSnapshot();

        var payload = serializer.Serialize(snapshot, typeof(CustomerSnapshot), compression);
        var restored = (CustomerSnapshot)serializer.Deserialize(payload, typeof(CustomerSnapshot), compression);

        restored.Should().BeEquivalentTo(snapshot);
    }

    [Fact]
    public void Lz4_actually_compresses_repetitive_payloads()
    {
        var serializer = new RedisSnapshotSerializer();
        var customer = Customer.CreateNew("cust-1");
        customer.Register(new string('x', 5000), CustomerKind.Company, DateTime.UtcNow);
        var snapshot = customer.CreateSnapshot();

        var raw = serializer.Serialize(snapshot, typeof(CustomerSnapshot), SnapshotCompression.None);
        var compressed = serializer.Serialize(snapshot, typeof(CustomerSnapshot), SnapshotCompression.Lz4);

        compressed.Length.Should().BeLessThan(raw.Length);
    }
}
