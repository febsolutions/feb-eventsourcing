using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

namespace FEB.EventSourcing.Sql;

/// <summary>
/// Relational event persistence over ADO.NET. Append is a single transaction:
/// version compare-and-set + event inserts — the atomicity MongoDB standalone has to
/// emulate comes for free here. Events are stored as JSON (System.Text.Json) with
/// their stable <see cref="EventNameAttribute"/> name, or the assembly-qualified CLR
/// type name when they have none; one table set serves all aggregate types
/// (discriminated by <c>aggregate_type</c>).
/// </summary>
public class SqlEventStorePersistence(
    ISqlDialect dialect,
    SqlEventStoreOptions options,
    IEventStoreMetrics? mayBeMetrics)
    : IEventStorePersistence
{
    private const string Source = "sql-events";

    private readonly IEventStoreMetrics _metrics = mayBeMetrics ?? new NoOpEventStoreMetrics();

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public async Task AppendEventsAsync<TAggregate, TId>(TId id, int expectedVersion, ICollection<EventEnvelope<TAggregate, TId>> events, CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        if (events.Count == 0)
            return;

        var sw = Stopwatch.StartNew();
        var aggregateType = typeof(TAggregate).Name;
        var aggregateId = IdToString(id);
        var newVersion = expectedVersion + events.Count;

        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1) Version compare-and-set
        int affected;
        if (expectedVersion < 0)
        {
            affected = await ExecuteAsync(connection, tx, dialect.InsertVersionIfAbsent(options), cancellationToken,
                ("aggregate_type", aggregateType), ("aggregate_id", aggregateId), ("version", newVersion));
        }
        else
        {
            affected = await ExecuteAsync(connection, tx,
                $"UPDATE {options.Qualified(options.VersionsTable)} SET version = @new_version " +
                "WHERE aggregate_type = @aggregate_type AND aggregate_id = @aggregate_id AND version = @expected",
                cancellationToken,
                ("new_version", newVersion), ("aggregate_type", aggregateType), ("aggregate_id", aggregateId), ("expected", expectedVersion));
        }

        if (affected == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            throw new ConcurrencyException<TId>(id, expectedVersion);
        }

        // 2) Events (unique index on (aggregate_type, aggregate_id, version) is the second guard)
        var insert =
            $"INSERT INTO {options.Qualified(options.EventsTable)} " +
            "(event_id, aggregate_type, aggregate_id, version, event_type, payload, occurred_at, tenant_id, user_id, correlation_id, causation_id, headers) " +
            $"VALUES (@event_id, @aggregate_type, @aggregate_id, @version, @event_type, {dialect.JsonParameter("@payload")}, @occurred_at, @tenant_id, @user_id, @correlation_id, @causation_id, {dialect.JsonParameter("@headers")})";

        try
        {
            foreach (var e in events)
            {
                var m = e.Metadata;
                await ExecuteAsync(connection, tx, insert, cancellationToken,
                    ("event_id", m.EventId),
                    ("aggregate_type", aggregateType),
                    ("aggregate_id", aggregateId),
                    ("version", m.Version),
                    ("event_type", EventTypeNames.GetConfiguredName(e.Payload.GetType())
                                   ?? e.Payload.GetType().AssemblyQualifiedName!),
                    ("payload", JsonSerializer.Serialize(e.Payload, e.Payload.GetType(), JsonOptions)),
                    ("occurred_at", m.OccurredAt),
                    ("tenant_id", (object?)m.TenantId ?? DBNull.Value),
                    ("user_id", (object?)m.UserId ?? DBNull.Value),
                    ("correlation_id", m.CorrelationId),
                    ("causation_id", (object?)m.CausationId ?? DBNull.Value),
                    ("headers", m.Headers is null ? DBNull.Value : JsonSerializer.Serialize(m.Headers, JsonOptions)));
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch (DbException ex) when (dialect.IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            throw new ConcurrencyException<TId>(id, expectedVersion, innerException: ex);
        }

        sw.Stop();
        _metrics.IncrementWrite<TAggregate>(Source);
        _metrics.RecordWrite<TAggregate>(Source, sw.Elapsed.TotalSeconds);
    }

    public async Task<IReadOnlyList<EventEnvelope<TAggregate, TId>>> LoadEventsAsync<TAggregate, TId>(TId aggregateId, int fromVersion = 0, CancellationToken cancellationToken = default)
        where TAggregate : AggregateRoot<TAggregate, TId>, IEntity<TId>, new()
    {
        var sw = Stopwatch.StartNew();
        var result = new List<EventEnvelope<TAggregate, TId>>();

        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT event_id, version, event_type, {dialect.JsonToText("payload")}, occurred_at, tenant_id, user_id, correlation_id, causation_id, {dialect.JsonToText("headers")} " +
            $"FROM {options.Qualified(options.EventsTable)} " +
            "WHERE aggregate_type = @aggregate_type AND aggregate_id = @aggregate_id AND version >= @from_version " +
            "ORDER BY version";
        AddParameters(cmd, ("aggregate_type", typeof(TAggregate).Name), ("aggregate_id", IdToString(aggregateId)), ("from_version", fromVersion));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventType = EventTypeNames.ResolveRequired(reader.GetString(2));
            var payload = (IDomainEvent<TAggregate>)JsonSerializer.Deserialize(reader.GetString(3), eventType, JsonOptions)!;
            var headersJson = reader.IsDBNull(9) ? null : reader.GetString(9);

            var metadata = new EventMetadata<TId>(
                EventId: reader.GetGuid(0),
                AggregateId: aggregateId,
                AggregateType: typeof(TAggregate).Name,
                Version: reader.GetInt32(1),
                OccurredAt: DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                TenantId: reader.IsDBNull(5) ? null : reader.GetString(5),
                UserId: reader.IsDBNull(6) ? null : reader.GetString(6),
                CorrelationId: reader.GetString(7),
                CausationId: reader.IsDBNull(8) ? null : reader.GetString(8),
                Headers: headersJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson, JsonOptions));

            result.Add(new EventEnvelope<TAggregate, TId>(payload, metadata));
        }

        sw.Stop();
        _metrics.IncrementRead<TAggregate>(Source);
        _metrics.RecordRead<TAggregate>(Source, sw.Elapsed.TotalSeconds);

        return result;
    }

    public async Task<bool> IsExistsAsync<TAggregate, TId>(TId id, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {options.Qualified(options.VersionsTable)} WHERE aggregate_type = @aggregate_type AND aggregate_id = @aggregate_id";
        AddParameters(cmd, ("aggregate_type", typeof(TAggregate).Name), ("aggregate_id", IdToString(id)));

        return await cmd.ExecuteScalarAsync(cancellationToken) != null;
    }

    public async Task<IReadOnlyList<TId>> GetAllIdsAsync<TAggregate, TId>(CancellationToken cancellationToken = default)
    {
        var ids = new List<TId>();

        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT aggregate_id FROM {options.Qualified(options.VersionsTable)} WHERE aggregate_type = @aggregate_type";
        AddParameters(cmd, ("aggregate_type", typeof(TAggregate).Name));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(IdFromString<TId>(reader.GetString(0)));

        return ids;
    }

    // ---- helpers -----------------------------------------------------------------

    internal static string IdToString<TId>(TId id) => id switch
    {
        string s => s,
        Guid g => g.ToString("D"),
        null => throw new ArgumentNullException(nameof(id)),
        _ => Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture)!
    };

    internal static TId IdFromString<TId>(string value)
    {
        if (typeof(TId) == typeof(string)) return (TId)(object)value;
        if (typeof(TId) == typeof(Guid)) return (TId)(object)Guid.Parse(value);
        if (typeof(TId) == typeof(int)) return (TId)(object)int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        if (typeof(TId) == typeof(long)) return (TId)(object)long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        throw new NotSupportedException($"Aggregate id type {typeof(TId).Name} is not supported by the SQL persistence (string, Guid, int, long).");
    }

    internal static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        AddParameters(cmd, parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static void AddParameters(DbCommand cmd, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@" + name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}
