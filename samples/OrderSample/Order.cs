using FEB.EventSourcing;
using FEB.EventSourcing.Snapshots;

namespace OrderSample;

public enum OrderStatus
{
    None,
    Placed,
    Shipped
}

/// <summary>
/// A deliberately small aggregate that shows the essentials:
/// state is only ever changed by raising events, [AutoSnapshot] makes the
/// source generator emit a snapshot DTO plus Create/Restore methods.
/// </summary>
[AutoSnapshot]
public partial class Order : AggregateRoot<Order, string>
{
    public string Customer { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
    public decimal Total { get; set; }

    public override void EnsureHasId()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("Id must be set!");
    }

    public void Place(string customer)
    {
        if (Status != OrderStatus.None)
            throw new InvalidOperationException("Order was already placed.");

        Raise(new OrderPlaced(customer));
    }

    public void AddLine(string article, int quantity, decimal unitPrice)
    {
        if (Status != OrderStatus.Placed)
            throw new InvalidOperationException("Lines can only be added to a placed order.");

        Raise(new OrderLineAdded(article, quantity, unitPrice));
    }

    public void Ship()
    {
        if (Status != OrderStatus.Placed)
            throw new InvalidOperationException("Only placed orders can be shipped.");

        Raise(new OrderShipped());
    }
}

/// <summary>Nested state must be partial so the generator can add snapshot methods.</summary>
public partial class OrderLine
{
    public string Article { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

// --- Events: immutable records that know how to apply themselves ---
//
// [EventName] gives each event a stable storage name, so these classes can later be
// renamed or moved to another namespace/project without touching stored data. The
// attribute is optional — without it the CLR type name is stored and the class is
// pinned by it. Never change a name once events exist under it.

[EventName("order.placed")]
public sealed record OrderPlaced(string Customer) : IDomainEvent<Order>
{
    public void ApplyTo(Order obj)
    {
        obj.Customer = Customer;
        obj.Status = OrderStatus.Placed;
    }
}

[EventName("order.line-added")]
public sealed record OrderLineAdded(string Article, int Quantity, decimal UnitPrice) : IDomainEvent<Order>
{
    public void ApplyTo(Order obj)
    {
        obj.Lines.Add(new OrderLine { Article = Article, Quantity = Quantity, UnitPrice = UnitPrice });
        obj.Total += Quantity * UnitPrice;
    }
}

// Illustrates the move case: this event used to live in another assembly, and events
// stored under that name must stay readable. Aliases are only ever read, never written.
[EventName("order.shipped", Aliases = ["OrderSample.Legacy.OrderShippedV1, OrderSample"])]
public sealed record OrderShipped : IDomainEvent<Order>
{
    public void ApplyTo(Order obj) => obj.Status = OrderStatus.Shipped;
}
