using System.Collections;

namespace FEB.EventSourcing.TestKit;

/// <summary>
/// Struktureller Vergleich zweier Objektgraphen; sammelt Pfade aller Abweichungen.
/// </summary>
internal static class DeepComparer
{
    public static void Compare(object? expected, object? actual, string path, List<string> differences)
    {
        if (expected is null && actual is null)
            return;

        if (expected is null || actual is null)
        {
            differences.Add($"{path}: expected {Describe(expected)}, actual {Describe(actual)}");
            return;
        }

        var type = expected.GetType();

        if (type != actual.GetType())
        {
            differences.Add($"{path}: type mismatch ({type.Name} vs {actual.GetType().Name})");
            return;
        }

        if (type.IsPrimitive || type.IsEnum || expected is string or decimal or Guid or DateTime or DateTimeOffset)
        {
            if (!expected.Equals(actual))
                differences.Add($"{path}: expected '{expected}', actual '{actual}'");
            return;
        }

        if (expected is IEnumerable expectedEnumerable && actual is IEnumerable actualEnumerable)
        {
            var expectedItems = expectedEnumerable.Cast<object?>().ToList();
            var actualItems = actualEnumerable.Cast<object?>().ToList();

            if (expectedItems.Count != actualItems.Count)
            {
                differences.Add($"{path}: expected {expectedItems.Count} items, actual {actualItems.Count}");
                return;
            }

            for (var i = 0; i < expectedItems.Count; i++)
                Compare(expectedItems[i], actualItems[i], $"{path}[{i}]", differences);

            return;
        }

        foreach (var property in type.GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            Compare(
                property.GetValue(expected),
                property.GetValue(actual),
                $"{path}.{property.Name}",
                differences);
        }
    }

    private static string Describe(object? value) => value is null ? "null" : $"'{value}'";
}
