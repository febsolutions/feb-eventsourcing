namespace FEB.EventSourcing;

public sealed class ConcurrencyException<TId>(
    TId aggregateId,
    int expectedVersion,
    int? actualVersion = null,
    Exception? innerException = null)
    : Exception(BuildMessage(aggregateId, expectedVersion, actualVersion), innerException)
{
    public TId AggregateId { get; } = aggregateId;
    public int ExpectedVersion { get; } = expectedVersion;
    public int? ActualVersion { get; } = actualVersion;

    private static string BuildMessage(
        TId aggregateId,
        int expectedVersion,
        int? actualVersion)
    {
        return actualVersion.HasValue
            ? $"Concurrency conflict on aggregate '{aggregateId}'. Expected version {expectedVersion}, but actual version is {actualVersion}."
            : $"Concurrency conflict on aggregate '{aggregateId}'. Expected version {expectedVersion}.";
    }
}