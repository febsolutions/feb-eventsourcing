using System.Data.Common;
using System.Text.Json;

namespace FEB.EventSourcing.Sql;

/// <summary>
/// Relational outbox with per-subscriber delivery tracking (same model as MongoDB:
/// a <c>deliveries</c> JSON map keyed by subscriber name). Leasing uses the dialect's
/// SKIP LOCKED / READPAST query, so multiple workers never process the same envelope
/// concurrently.
/// </summary>
public class SqlOutboxPersistence(
    ISqlDialect dialect,
    SqlEventStoreOptions options,
    SqlOutboxOptions outboxOptions)
    : IOutboxPersistence
{
    private static readonly JsonSerializerOptions Json = SqlEventStorePersistence.JsonOptions;

    internal sealed class Delivery
    {
        public DateTime? DispatchedAt { get; set; }
        public int AttemptCount { get; set; }
        public string? LastError { get; set; }
        public DateTime? DeadLetteredAt { get; set; }
        public bool IsTerminal => DispatchedAt != null || DeadLetteredAt != null;
    }

    /// <summary>The event store options this outbox shares — identifies "same store" for atomic appends.</summary>
    internal SqlEventStoreOptions EventStoreOptions => options;

    public async Task EnqueueAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await InsertAsync(dialect, options, connection, null, envelope, cancellationToken);
    }

    public async Task EnqueueManyAsync(IReadOnlyCollection<OutboxEnvelope> envelopes, CancellationToken cancellationToken = default)
    {
        if (envelopes.Count == 0)
            return;

        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var envelope in envelopes)
            await InsertAsync(dialect, options, connection, tx, envelope, cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts one envelope on the given connection/transaction. Shared with
    /// <see cref="SqlEventStorePersistence"/>, which writes envelopes inside the append transaction.
    /// </summary>
    internal static Task InsertAsync(ISqlDialect dialect, SqlEventStoreOptions options, DbConnection connection,
        DbTransaction? transaction, OutboxEnvelope envelope, CancellationToken cancellationToken)
        => SqlEventStorePersistence.ExecuteAsync(connection, transaction,
            $"INSERT INTO {options.Qualified(options.OutboxTable)} (id, payload_json, metadata_json, created_at, deliveries_json) " +
            $"VALUES (@id, @payload_json, @metadata_json, @created_at, {dialect.JsonParameter("@deliveries_json")})",
            cancellationToken,
            ("id", envelope.Metadata.EventId),
            ("payload_json", JsonSerializer.Serialize(envelope.Payload, Json)),
            ("metadata_json", JsonSerializer.Serialize(envelope.Metadata, Json)),
            ("created_at", DateTime.UtcNow),
            ("deliveries_json", "{}"));

    public async Task<IReadOnlyList<PendingOutboxEnvelope>> DequeueBatchAsync(IReadOnlyCollection<string> subscriberNames, int maxCount, CancellationToken cancellationToken = default)
    {
        var result = new List<PendingOutboxEnvelope>();
        var now = DateTime.UtcNow;

        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var leased = new List<(Guid Id, string Payload, string Metadata, DateTime CreatedAt, string Deliveries)>();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = dialect.LeaseOutboxBatch(options);
            SqlEventStorePersistence.AddParameters(cmd,
                ("max_count", maxCount),
                ("now", now),
                ("locked_until", now.Add(outboxOptions.LeaseDuration)));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                // columns: id, payload_json, metadata_json, created_at, deliveries_json
                leased.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3), reader.IsDBNull(4) ? "{}" : reader.GetString(4)));
        }

        // RETURNING (Postgres) / OUTPUT (SQL Server) do not guarantee row order —
        // restore oldest-first within the leased batch so delivery stays FIFO.
        leased.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));

        foreach (var row in leased)
        {
            var deliveries = ParseDeliveries(row.Deliveries);
            var pending = subscriberNames.Where(n => !IsTerminal(deliveries, n)).ToList();

            if (pending.Count > 0)
            {
                result.Add(new PendingOutboxEnvelope(
                    new OutboxEnvelope(
                        JsonSerializer.Deserialize<OutboxPayload>(row.Payload, Json)!,
                        JsonSerializer.Deserialize<OutboxMetadata>(row.Metadata, Json)!),
                    pending));
                continue;
            }

            if (subscriberNames.All(n => IsTerminal(deliveries, n)))
                await CompleteAsync(connection, row.Id, cancellationToken);
            else
                await ReleaseAsync(connection, row.Id, cancellationToken);
        }

        return result;
    }

    public async Task MarkDispatchedAsync(OutboxEnvelope envelope, string subscriberName, IReadOnlyCollection<string> allSubscriberNames, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var deliveries = await UpdateDeliveriesAsync(connection, envelope.Metadata.EventId, d =>
        {
            d.DispatchedAt = DateTime.UtcNow;
            d.LastError = null;
        }, subscriberName, cancellationToken);

        if (deliveries != null && allSubscriberNames.All(n => IsTerminal(deliveries, n)))
            await CompleteAsync(connection, envelope.Metadata.EventId, cancellationToken);
    }

    public async Task MarkFailedAsync(OutboxEnvelope envelope, string subscriberName, Exception exception, IReadOnlyCollection<string> allSubscriberNames, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var id = envelope.Metadata.EventId;

        var deliveries = await UpdateDeliveriesAsync(connection, id, d =>
        {
            d.AttemptCount++;
            d.LastError = exception.ToString();
        }, subscriberName, cancellationToken);

        if (deliveries == null)
            return;

        var delivery = deliveries[subscriberName];
        if (delivery.AttemptCount < outboxOptions.MaxAttempts)
        {
            await ReleaseAsync(connection, id, cancellationToken);
            return;
        }

        // Dead-letter this subscription
        try
        {
            await SqlEventStorePersistence.ExecuteAsync(connection, null,
                $"INSERT INTO {options.Qualified(options.DeadLetterTable)} (id, event_id, subscriber, payload_json, metadata_json, created_at, attempt_count, failed_at, last_error) " +
                $"SELECT @dl_id, id, @subscriber, payload_json, metadata_json, created_at, @attempts, @failed_at, @last_error FROM {options.Qualified(options.OutboxTable)} WHERE id = @id",
                cancellationToken,
                ("dl_id", $"{id}:{subscriberName}"),
                ("subscriber", subscriberName),
                ("attempts", delivery.AttemptCount),
                ("failed_at", DateTime.UtcNow),
                ("last_error", delivery.LastError ?? "Unknown error"),
                ("id", id));
        }
        catch (DbException ex) when (dialect.IsDuplicateKey(ex))
        {
            // already dead-lettered
        }

        var terminal = await UpdateDeliveriesAsync(connection, id, d => d.DeadLetteredAt = DateTime.UtcNow, subscriberName, cancellationToken);
        await ReleaseAsync(connection, id, cancellationToken);

        if (terminal != null && allSubscriberNames.All(n => IsTerminal(terminal, n)))
            await CompleteAsync(connection, id, cancellationToken);
    }

    /// <summary>Bounded retention cleanup; called periodically by the OutboxWorker.</summary>
    public async Task<int> CleanupCompletedAsync(CancellationToken cancellationToken = default)
    {
        if (outboxOptions.CompletedRetention is not { } retention)
            return 0;

        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return await SqlEventStorePersistence.ExecuteAsync(connection, null,
            dialect.DeleteCompletedOutbox(options),
            cancellationToken,
            ("cutoff", DateTime.UtcNow - retention),
            ("batch", outboxOptions.CleanupBatchSize));
    }

    // ---- helpers -----------------------------------------------------------------

    private async Task<Dictionary<string, Delivery>?> UpdateDeliveriesAsync(DbConnection connection, Guid id, Action<Delivery> mutate, string subscriberName, CancellationToken ct)
    {
        // read-modify-write inside a transaction (the envelope is leased by this worker,
        // so no concurrent writer touches the same row's deliveries)
        await using var tx = await connection.BeginTransactionAsync(ct);

        string? json;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"SELECT {dialect.JsonToText("deliveries_json")} FROM {options.Qualified(options.OutboxTable)} WHERE id = @id";
            SqlEventStorePersistence.AddParameters(read, ("id", id));
            json = await read.ExecuteScalarAsync(ct) as string;
        }

        if (json is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var deliveries = ParseDeliveries(json);
        if (!deliveries.TryGetValue(subscriberName, out var d))
            deliveries[subscriberName] = d = new Delivery();
        mutate(d);

        await SqlEventStorePersistence.ExecuteAsync(connection, tx,
            $"UPDATE {options.Qualified(options.OutboxTable)} SET deliveries_json = {dialect.JsonParameter("@deliveries_json")} WHERE id = @id",
            ct, ("deliveries_json", JsonSerializer.Serialize(deliveries, Json)), ("id", id));

        await tx.CommitAsync(ct);
        return deliveries;
    }

    private Task CompleteAsync(DbConnection connection, Guid id, CancellationToken ct)
        => SqlEventStorePersistence.ExecuteAsync(connection, null,
            $"UPDATE {options.Qualified(options.OutboxTable)} SET dispatched_at = @now, locked_until = NULL WHERE id = @id",
            ct, ("now", DateTime.UtcNow), ("id", id));

    private Task ReleaseAsync(DbConnection connection, Guid id, CancellationToken ct)
        => SqlEventStorePersistence.ExecuteAsync(connection, null,
            $"UPDATE {options.Qualified(options.OutboxTable)} SET locked_until = NULL WHERE id = @id",
            ct, ("id", id));

    private static Dictionary<string, Delivery> ParseDeliveries(string json)
        => JsonSerializer.Deserialize<Dictionary<string, Delivery>>(json, Json) ?? new Dictionary<string, Delivery>();

    private static bool IsTerminal(Dictionary<string, Delivery> deliveries, string name)
        => deliveries.TryGetValue(name, out var d) && d.IsTerminal;
}
