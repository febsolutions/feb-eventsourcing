# FEB.Cqrs — command and query dispatch

`FEB.Cqrs` is a deliberately tiny package: two marker interfaces, two handler
interfaces and two dispatchers that resolve handlers from DI. It has **no
dependency on FEB.EventSourcing** and can be used on its own; equally, you can use
FEB.EventSourcing without it (any mediator or a plain service layer works). It
exists because most event-sourced applications want a thin "one handler per
command" structure and nothing more.

## Contracts

```csharp
public interface ICommand;                       // fire-and-forget command
public interface ICommand<TResult> : ICommand;   // command returning a value

public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{ Task HandleAsync(TCommand command, CancellationToken ct); }

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{ Task<TResult> HandleAsync(TCommand command, CancellationToken ct); }

public interface IQuery<TResult>;

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{ Task<TResult> HandleAsync(TQuery query, CancellationToken ct); }
```

Registration: `services.AddCqrs()` adds `ICommandDispatcher` and `IQueryDispatcher`
(scoped). Handlers are **not** scanned — register them yourself (or with your
favourite scanning helper):

```csharp
services.AddCqrs();
services.AddScoped<ICommandHandler<PlaceOrder>, PlaceOrderHandler>();
services.AddScoped<ICommandHandler<AddLine, int>, AddLineHandler>();
services.AddScoped<IQueryHandler<GetOrder, OrderDto>, GetOrderHandler>();
```

## Dispatching

```csharp
public sealed class OrdersController(ICommandDispatcher commands, IQueryDispatcher queries)
{
    public Task Place(PlaceOrder cmd, CancellationToken ct)
        => commands.ExecuteAsync(cmd, ct);                       // void command

    public Task<int> AddLine(AddLine cmd, CancellationToken ct)
        => commands.ExecuteAsync<int>(cmd, ct);                  // command with result

    public Task<OrderDto> Get(GetOrder q, CancellationToken ct)
        => queries.ExecuteAsync(q, ct);
}
```

**Overload note.** A command type implementing `ICommand<TResult>` also implements
`ICommand`. When you pass a *concrete* command instance, C# overload resolution
prefers the void `ExecuteAsync<TCommand>(TCommand)` over
`ExecuteAsync<TResult>(ICommand<TResult>)`, and the void handler will be resolved
(and fail if you only registered the result-returning one). For commands with
results always call the explicit form — `ExecuteAsync<TResult>(cmd)` or
`ExecuteAsync<TCommand, TResult>(cmd)`. Queries have no such ambiguity.

## Putting it together with the event store

A typical command handler is three lines of orchestration around the aggregate:

```csharp
public sealed class AddLineHandler(IEventStore<Order, string> store, ICommandContextFactory ctxFactory)
    : ICommandHandler<AddLine, int>
{
    public async Task<int> HandleAsync(AddLine cmd, CancellationToken ct)
    {
        var order = await store.LoadByIdAsync(cmd.OrderId, ct)
                    ?? throw new NotFoundException(cmd.OrderId);

        order.AddLine(cmd.Article, cmd.Quantity, cmd.UnitPrice);

        await store.SaveAsync(order, ctxFactory.Create(cmd), ct);
        return order.Lines.Count;
    }
}
```

`ICommandContextFactory` is *your* small service that turns the current request
(user, tenant, correlation id) into a `CommandContext` — the framework deliberately
does not know your HTTP or messaging stack.

Query handlers read from **projections**, never from the event store — that is the
point of CQRS. Loading an aggregate to answer a query is a smell (except for
admin/diagnostic views that want the exact current state).

## What it does not do

No pipelines/behaviours (validation, logging, transactions), no notifications, no
automatic handler discovery. If you need those, use MediatR or a similar library;
the event store does not care which dispatcher calls it.
