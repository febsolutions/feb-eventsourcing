using FEB.EventSourcing.Snapshots;

namespace FEB.EventSourcing.Tests.TestDomain;

public enum CustomerKind
{
    Person,
    Company
}

/// <summary>In-source base class: its state must be part of the snapshot (inheritance test).</summary>
public abstract class CategorizedAggregate<TAggregate> : AggregateRoot<TAggregate, string>
    where TAggregate : AggregateRoot<TAggregate>, IEntity<string>, new()
{
    public string? Category { get; set; }

    public override void EnsureHasId()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("Id must be set!");
    }
}

[AutoSnapshot]
public partial class Customer : CategorizedAggregate<Customer>
{
    public string Name { get; set; } = string.Empty;
    public CustomerKind Kind { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal? CreditLimit { get; set; }
    public Address? Address { get; set; }
    public List<Contact> Contacts { get; set; } = [];
    public List<string> Tags { get; set; } = [];

    [IgnoreSnapshot]
    public string? TransientNote { get; set; }

    public void Register(string name, CustomerKind kind, DateTime createdAt)
        => Raise(new CustomerRegistered(name, kind, createdAt));

    public void Relocate(string street, string city)
        => Raise(new CustomerRelocated(street, city));

    public void AddContact(string name, string email)
        => Raise(new CustomerContactAdded(name, email));
}

public partial class Address
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

public partial class Contact
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed record CustomerRegistered(string Name, CustomerKind Kind, DateTime CreatedAt) : IDomainEvent<Customer>
{
    public void ApplyTo(Customer obj)
    {
        obj.Name = Name;
        obj.Kind = Kind;
        obj.CreatedAt = CreatedAt;
    }
}

public sealed record CustomerRelocated(string Street, string City) : IDomainEvent<Customer>
{
    public void ApplyTo(Customer obj) => obj.Address = new Address { Street = Street, City = City };
}

public sealed record CustomerContactAdded(string Name, string Email) : IDomainEvent<Customer>
{
    public void ApplyTo(Customer obj) => obj.Contacts.Add(new Contact { Name = Name, Email = Email });
}
