using FEB.EventSourcing.Snapshots;

namespace FEB.EventSourcing.Tests.TestDomain;

/// <summary>
/// Second snapshot aggregate that uses the same nested type (<see cref="Address"/>) as
/// <see cref="Customer"/> - covers the generator's de-duplication of shared nested models.
/// </summary>
[AutoSnapshot]
public partial class Supplier : AggregateRoot<Supplier, string>
{
    public string Name { get; set; } = string.Empty;
    public Address? Address { get; set; }

    public override void EnsureHasId()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("Id must be set!");
    }
}
