namespace FEB.EventSourcing.Tests.TestDomain;

public class Order : AggregateRoot<Order, string>
{
    public string Customer { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public List<string> Items { get; set; } = [];

    public override void EnsureHasId()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("Id must be set!");
    }

    public void Create(string customer)
        => Raise(new OrderCreated(customer));

    public void AddItem(string name, decimal price)
        => Raise(new OrderItemAdded(name, price));

    public void Rename(string customer)
        => Raise(new OrderRenamed(customer));
}

public sealed record OrderCreated(string Customer) : IDomainEvent<Order>
{
    public void ApplyTo(Order obj) => obj.Customer = Customer;
}

public sealed record OrderItemAdded(string Name, decimal Price) : IDomainEvent<Order>
{
    public void ApplyTo(Order obj)
    {
        obj.Items.Add(Name);
        obj.Total += Price;
    }
}

/// <summary>
/// Event with a stable storage name: the class may be moved or renamed,
/// what is stored is always "order.renamed". The alias covers data still stored under
/// the old (moved) CLR name.
/// </summary>
[EventName("order.renamed", Aliases = ["FEB.EventSourcing.Tests.TestDomain.Legacy.OrderRenamedV1, FEB.EventSourcing.Tests"])]
public sealed record OrderRenamed(string Customer) : IDomainEvent<Order>
{
    public void ApplyTo(Order obj) => obj.Customer = Customer;
}
