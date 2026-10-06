using System.Collections.Concurrent;
using System.Reflection;

namespace FEB.EventSourcing;

/// <summary>
/// Maps events to the names they are stored under and back. Names come from
/// <see cref="EventNameAttribute"/>; events without the attribute have no
/// configured name and each persistence keeps using its previous CLR-based
/// default (so existing data and applications are unaffected).
/// <para>
/// The forward direction (type → name) works off the attribute alone. The reverse
/// direction (stored name → type) needs the event assemblies, which
/// <c>AddEventSourcing</c> hands over from the assemblies you pass it — the same
/// explicit list used everywhere else, no AppDomain scan.
/// </para>
/// </summary>
public static class EventTypeNames
{
    private static readonly ConcurrentDictionary<Type, string?> ConfiguredNames = new();
    private static readonly ConcurrentDictionary<string, Type> TypesByStoredName = new();
    private static readonly ConcurrentDictionary<Assembly, bool> ScannedAssemblies = new();

    /// <summary>
    /// Reads the configured name of an event type, or <c>null</c> when it carries no
    /// <see cref="EventNameAttribute"/>. Cached per type.
    /// </summary>
    public static string? GetConfiguredName(Type eventType)
        => ConfiguredNames.GetOrAdd(eventType, static t =>
        {
            var name = t.GetCustomAttribute<EventNameAttribute>(inherit: false)?.Name;
            if (name is null)
                return null;

            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException(
                    $"[EventName] on '{t.FullName}' has an empty name. Use a stable identifier such as \"order.created\".");

            return name;
        });

    /// <summary>
    /// Resolves a stored name (configured name or one of its aliases) back to the
    /// event type, or <c>null</c> if no registered event claims that name — the
    /// caller then falls back to its CLR-based default.
    /// </summary>
    public static Type? Resolve(string storedName)
        => TypesByStoredName.GetValueOrDefault(storedName);

    /// <summary>
    /// Resolves a name read from the store back to its event type: a configured
    /// <see cref="EventNameAttribute"/> name or alias first, otherwise the stored
    /// value is interpreted as a CLR type name — which is what the persistences
    /// wrote before the attribute existed. Throws with an actionable message when
    /// neither resolves, which is what an application sees when an event class was
    /// moved or renamed without giving it a stable name.
    /// </summary>
    public static Type ResolveRequired(string storedName)
        => Resolve(storedName)
           ?? Type.GetType(storedName, throwOnError: false)
           ?? throw new InvalidOperationException(
               $"Cannot resolve stored event type '{storedName}'. No registered event declares it via " +
               "[EventName] (name or alias) and it is not a loadable CLR type name. If the event class was " +
               "moved or renamed, give it [EventName(\"<stable name>\", Aliases = [\"" + storedName +
               "\"])] so the stored events stay readable, and make sure its assembly is passed to AddEventSourcing.");

    /// <summary>
    /// Registers the events of the given assemblies for name resolution. Idempotent;
    /// called by <c>AddEventSourcing</c> for the application assemblies.
    /// </summary>
    public static void RegisterAssemblies(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            if (!ScannedAssemblies.TryAdd(assembly, true))
                continue;

            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsClass: true, IsAbstract: false } or { IsValueType: true, IsEnum: false }
                    && typeof(IEvent).IsAssignableFrom(type))
                    Register(type);
            }
        }
    }

    /// <summary>Registers a single event type (name + aliases). Idempotent.</summary>
    public static void Register(Type eventType)
    {
        var attribute = eventType.GetCustomAttribute<EventNameAttribute>(inherit: false);
        if (attribute is null)
            return;

        // Validates the name as a side effect.
        var name = GetConfiguredName(eventType)!;

        Claim(name, eventType, "name");

        foreach (var alias in attribute.Aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
                throw new InvalidOperationException(
                    $"[EventName] on '{eventType.FullName}' has an empty alias.");

            Claim(alias, eventType, "alias");
        }
    }

    private static void Claim(string storedName, Type eventType, string kind)
    {
        var owner = TypesByStoredName.GetOrAdd(storedName, eventType);
        if (owner != eventType)
            throw new InvalidOperationException(
                $"Event {kind} '{storedName}' is claimed by two event types: '{owner.FullName}' and " +
                $"'{eventType.FullName}'. Stored names must be unique — otherwise stored events cannot " +
                "be read back unambiguously.");
    }
}
