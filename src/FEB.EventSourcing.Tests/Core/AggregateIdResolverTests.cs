using AwesomeAssertions;

namespace FEB.EventSourcing.Tests.Core;

public class AggregateIdResolverTests
{
    private readonly DefaultAggregateIdResolver _resolver = new();

    [Fact]
    public void Resolves_direct_type_match()
    {
        _resolver.TryResolve<string>("abc", out var id).Should().BeTrue();
        id.Should().Be("abc");
    }

    [Fact]
    public void Resolves_guid_from_string()
    {
        var guid = Guid.NewGuid();

        _resolver.TryResolve<Guid>(guid.ToString(), out var id).Should().BeTrue();
        id.Should().Be(guid);
    }

    [Fact]
    public void Resolves_value_object_with_static_parse()
    {
        _resolver.TryResolve<ParsableId>("42", out var id).Should().BeTrue();
        id.Value.Should().Be(42);
    }

    [Fact]
    public void Unresolvable_input_returns_false_instead_of_throwing()
    {
        _resolver.TryResolve<Guid>("not-a-guid", out _).Should().BeFalse("try semantics must not throw");
        _resolver.TryResolve<int>(new object(), out _).Should().BeFalse();
    }

    public readonly record struct ParsableId(int Value)
    {
        public static ParsableId Parse(string s) => new(int.Parse(s));
    }
}
