using FEB.Cqrs;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FEB.EventSourcing.Tests.Cqrs;

public class CommandDispatcherTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddCqrs();
        services.AddScoped<ICommandHandler<Ping>, PingHandler>();
        services.AddScoped<ICommandHandler<Add, int>, AddHandler>();
        services.AddScoped<IQueryHandler<Square, int>, SquareHandler>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Generic_and_non_generic_command_overloads_resolve_the_same_handler()
    {
        await using var sp = Build();
        var dispatcher = sp.GetRequiredService<ICommandDispatcher>();

        await dispatcher.ExecuteAsync(new Ping("a"));            // non-generic (runtime type)
        await dispatcher.ExecuteAsync<Ping>(new Ping("b"));      // generic

        PingHandler.Received.Should().Contain(["a", "b"]);
    }

    [Fact]
    public async Task Command_with_result_works_via_both_overloads()
    {
        await using var sp = Build();
        var dispatcher = sp.GetRequiredService<ICommandDispatcher>();

        // With a concrete command type the compiler prefers the void ICommand overload,
        // so commands with results are dispatched via the explicit generic overloads:
        (await dispatcher.ExecuteAsync<int>(new Add(2, 3))).Should().Be(5);
        (await dispatcher.ExecuteAsync<Add, int>(new Add(4, 5))).Should().Be(9);
    }

    [Fact]
    public async Task Query_dispatcher_resolves_handler()
    {
        await using var sp = Build();
        var queries = sp.GetRequiredService<IQueryDispatcher>();

        (await queries.ExecuteAsync(new Square(6))).Should().Be(36);
        (await queries.ExecuteAsync<Square, int>(new Square(7))).Should().Be(49);
    }

    public sealed record Ping(string Tag) : ICommand;
    public sealed record Add(int A, int B) : ICommand<int>;
    public sealed record Square(int X) : IQuery<int>;

    public sealed class PingHandler : ICommandHandler<Ping>
    {
        public static List<string> Received { get; } = [];
        public Task HandleAsync(Ping command, CancellationToken cancellationToken)
        {
            Received.Add(command.Tag);
            return Task.CompletedTask;
        }
    }

    public sealed class AddHandler : ICommandHandler<Add, int>
    {
        public Task<int> HandleAsync(Add command, CancellationToken cancellationToken) => Task.FromResult(command.A + command.B);
    }

    public sealed class SquareHandler : IQueryHandler<Square, int>
    {
        public Task<int> HandleAsync(Square query, CancellationToken cancellationToken) => Task.FromResult(query.X * query.X);
    }
}
