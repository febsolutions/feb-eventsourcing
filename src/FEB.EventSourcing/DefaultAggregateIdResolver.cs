using System.Reflection;

namespace FEB.EventSourcing;

public sealed class DefaultAggregateIdResolver : IAggregateIdResolver
{
    public bool TryResolve<TId>(object rawAggregateId, out TId id)
    {
        id = default!;

        if (rawAggregateId is TId direct)
        {
            id = direct;
            return true;
        }

        if (rawAggregateId is string s)
        {
            // Guid
            if (typeof(TId) == typeof(Guid) &&
                Guid.TryParse(s, out var guid))
            {
                id = (TId)(object)guid;
                return true;
            }

            // Value object with a static Parse(string)
            var parse = typeof(TId).GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                new[] { typeof(string) });

            if (parse != null)
            {
                try
                {
                    id = (TId)parse.Invoke(null, new object[] { s })!;
                    return true;
                }
                catch
                {
                    // Try semantics: unparsable input is not an error
                    return false;
                }
            }
        }

        return false;
    }
}