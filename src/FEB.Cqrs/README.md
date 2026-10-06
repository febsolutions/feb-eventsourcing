# FEB.Cqrs

A deliberately tiny command/query dispatcher for .NET: two marker interfaces, two
handler interfaces and two dispatchers that resolve handlers from
`Microsoft.Extensions.DependencyInjection`. No dependency on FEB.EventSourcing — use
it on its own or together with it.

```csharp
services.AddCqrs();   // ICommandDispatcher + IQueryDispatcher (scoped)
services.AddScoped<ICommandHandler<PlaceOrder>, PlaceOrderHandler>();
services.AddScoped<IQueryHandler<GetOrder, OrderDto>, GetOrderHandler>();

await commands.ExecuteAsync(new PlaceOrder("o1", "ACME"), ct);
var order = await queries.ExecuteAsync(new GetOrder("o1"), ct);
```

Handlers are not scanned — register them explicitly. For commands that return a
value, call `ExecuteAsync<TResult>(cmd)` explicitly (see the documentation for why).

Documentation: https://github.com/febsolutions/feb-eventsourcing/blob/main/docs/cqrs.md
