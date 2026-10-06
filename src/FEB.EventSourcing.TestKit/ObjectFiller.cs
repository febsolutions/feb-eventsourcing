using System.Collections;
using System.Reflection;
using FEB.EventSourcing;

namespace FEB.EventSourcing.TestKit;

/// <summary>
/// Fills objects recursively with deterministic random data. Every writable
/// instance property is set - except framework state
/// (properties declared in FEB.EventSourcing base classes) and
/// [IgnoreSnapshot] members without a setter.
/// </summary>
internal static class ObjectFiller
{
    private const int MaxDepth = 6;

    public static void Fill(object target, Random random) => Fill(target, random, 0);

    private static void Fill(object target, Random random, int depth)
    {
        if (depth > MaxDepth)
            return;

        foreach (var property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.SetMethod is null || property.GetMethod is null)
                continue;

            if (property.GetIndexParameters().Length > 0)
                continue;

            // Framework state (Version, Id, ...) is not part of the snapshot contract
            if (IsFrameworkDeclared(property))
                continue;

            var value = CreateValue(property.PropertyType, random, depth);
            if (value != null)
                property.SetValue(target, value);
        }
    }

    private static bool IsFrameworkDeclared(PropertyInfo property)
        => property.DeclaringType?.Assembly == typeof(AggregateRoot).Assembly;

    private static object? CreateValue(Type type, Random random, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
            return $"str-{random.Next(1000, 9999)}";
        if (underlying == typeof(bool))
            return random.Next(2) == 1;
        if (underlying == typeof(int))
            return random.Next(1, 100_000);
        if (underlying == typeof(long))
            return (long)random.Next(1, 100_000);
        if (underlying == typeof(short))
            return (short)random.Next(1, short.MaxValue);
        if (underlying == typeof(byte))
            return (byte)random.Next(1, byte.MaxValue);
        if (underlying == typeof(double))
            return Math.Round(random.NextDouble() * 1000, 4);
        if (underlying == typeof(float))
            return (float)Math.Round(random.NextDouble() * 1000, 2);
        if (underlying == typeof(decimal))
            return Math.Round((decimal)random.NextDouble() * 1000m, 2);
        if (underlying == typeof(Guid))
            return DeterministicGuid(random);
        if (underlying == typeof(DateTime))
            return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(random.Next(0, 10_000_000));
        if (underlying == typeof(DateTimeOffset))
            return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(random.Next(0, 10_000_000));
        if (underlying.IsEnum)
        {
            var values = Enum.GetValues(underlying);
            return values.Length == 0 ? null : values.GetValue(random.Next(values.Length));
        }

        if (IsSupportedListType(underlying, out var elementType))
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType!))!;

            var count = random.Next(1, 4);
            for (var i = 0; i < count; i++)
            {
                var element = CreateElement(elementType!, random, depth);
                if (element != null)
                    list.Add(element);
            }

            return list;
        }

        if (underlying.IsClass)
            return CreateElement(underlying, random, depth);

        return null;
    }

    private static object? CreateElement(Type type, Random random, int depth)
    {
        if (depth >= MaxDepth)
            return null;

        if (type == typeof(string) || type.IsValueType || type.IsEnum)
            return CreateValue(type, random, depth);

        if (type.GetConstructor(Type.EmptyTypes) is null)
            return null;

        var instance = Activator.CreateInstance(type)!;
        Fill(instance, random, depth + 1);
        return instance;
    }

    private static bool IsSupportedListType(Type type, out Type? elementType)
    {
        elementType = null;

        if (!type.IsGenericType)
            return false;

        var definition = type.GetGenericTypeDefinition();
        if (definition != typeof(List<>) && definition != typeof(IReadOnlyList<>) && definition != typeof(ICollection<>))
            return false;

        elementType = type.GetGenericArguments()[0];
        return true;
    }

    private static Guid DeterministicGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }
}
