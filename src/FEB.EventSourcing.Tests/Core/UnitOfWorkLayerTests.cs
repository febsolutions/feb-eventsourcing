using AwesomeAssertions;
using FEB.EventSourcing.Redis;
using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Snapshots.Generated;
using FEB.EventSourcing.Tests.Infrastructure;
using FEB.EventSourcing.Tests.Redis;
using FEB.EventSourcing.Tests.TestDomain;

namespace FEB.EventSourcing.Tests.Core;

/// <summary>
/// Store layers with effects outside the database defer them while a caller-owned
/// transaction is active (decision 0016): nothing may be cached or enqueued for a state
/// that can still be rolled back.
/// </summary>
public class UnitOfWorkLayerTests
{
    private static readonly SnapshotMetadataRegistry Registry =
        new(SnapshotMetadataModule_FEB_EventSourcing_Tests.CreateAll());

    /// <summary>A unit of work under test control: complete runs the deferred actions, discard drops them.</summary>
    private sealed class FakeUnitOfWork : IUnitOfWorkContext
    {
        private readonly List<Func<CancellationToken, Task>> _actions = [];
        public bool IsActive { get; private set; } = true;

        public Task AfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            if (!IsActive)
                return action(cancellationToken);
            _actions.Add(action);
            return Task.CompletedTask;
        }

        public async Task CompleteAsync()
        {
            IsActive = false;
            foreach (var action in _actions.ToArray())
                await action(CancellationToken.None);
            _actions.Clear();
        }

        public void Discard()
        {
            IsActive = false;
            _actions.Clear();
        }
    }

    private static Customer NewCustomer(string id)
    {
        var customer = Customer.CreateNew(id);
        customer.Register("ACME GmbH", CustomerKind.Company, DateTime.UtcNow);
        customer.Relocate("Hauptstraße 1", "Berlin");
        return customer;
    }

    // ------------------------------------------------------------------ Redis

    private static (RedisCacheStore<Customer, string> Store, CountingInnerStore<Customer, string> Inner) Redis(
        FakeRedisCacheDatabase redis, IUnitOfWorkContext unitOfWork, Func<string, Customer?> loader)
    {
        var inner = new CountingInnerStore<Customer, string>(loader);
        var store = new RedisCacheStore<Customer, string>(
            inner, new AggregateFactory<Customer, string>(Customer.CreateNew), Registry, redis,
            new RedisSnapshotSerializer(), new RedisEventStoreOptions(), new RecordingMetrics(), unitOfWork);
        return (store, inner);
    }

    [Fact]
    public async Task Redis_bypasses_the_cache_for_loads_inside_a_unit_of_work()
    {
        var redis = new FakeRedisCacheDatabase();
        Customer Committed(string id) { var c = NewCustomer(id); c.Commit(1); return c; }

        // A committed state is cached (outside any unit of work).
        var (outside, _) = Redis(redis, NoUnitOfWorkContext.Instance, Committed);
        await outside.LoadByIdAsync("c1");
        redis.Store.Should().ContainKey("Customer:c1");

        // Inside: the flow may have written uncommitted events, so the cache is neither read nor filled.
        var unitOfWork = new FakeUnitOfWork();
        var (inside, inner) = Redis(redis, unitOfWork, Committed);
        await inside.LoadByIdAsync("c1");
        await inside.LoadByIdAsync("c2");

        inner.LoadCalls.Should().Be(2, "both loads must go to the store");
        redis.Store.Should().NotContainKey("Customer:c2", "a state loaded inside a transaction must not be cached");
    }

    [Fact]
    public async Task Redis_writes_the_saved_state_only_after_the_commit()
    {
        var redis = new FakeRedisCacheDatabase();
        var unitOfWork = new FakeUnitOfWork();
        var (store, _) = Redis(redis, unitOfWork, _ => null);

        var customer = NewCustomer("c1");
        await store.SaveAsync(customer, TestHost.NewCommandContext());
        redis.Store.Should().BeEmpty("nothing may be cached before the caller has committed");

        // Further, uncommitted change after the save: must not end up in the cache.
        customer.Relocate("Elsewhere 2", "Hamburg");
        await unitOfWork.CompleteAsync();

        var (reader, _) = Redis(redis, NoUnitOfWorkContext.Instance, _ => null);
        var cached = await reader.LoadByIdAsync("c1");
        cached!.Address!.City.Should().Be("Berlin", "the state is captured at save time, not at commit time");
        cached.Version.Should().Be(1);
    }

    [Fact]
    public async Task Redis_writes_nothing_when_the_unit_of_work_is_rolled_back()
    {
        var redis = new FakeRedisCacheDatabase();
        var unitOfWork = new FakeUnitOfWork();
        var (store, _) = Redis(redis, unitOfWork, _ => null);

        await store.SaveAsync(NewCustomer("c1"), TestHost.NewCommandContext());
        unitOfWork.Discard();

        redis.Store.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ snapshots

    private sealed class RecordingSnapshotPersistence(string name) : ISnapshotPersistence
    {
        public List<(string Instance, int Version)> Saved { get; } = [];
        public RecordingSnapshotPersistence? Background { get; private set; }

        public Task SaveAsync<TAggregate, TId>(object snapshot, Type snapshotType, TId id, int streamVersion, int schemaVersion, CancellationToken cancellationToken = default)
        {
            Saved.Add((name, streamVersion));
            return Task.CompletedTask;
        }

        public Task<StoredSnapshot?> LoadAsync<TAggregate, TId>(TId id, Type snapshotType, int expectedSchemaVersion, CancellationToken cancellationToken = default)
            => Task.FromResult<StoredSnapshot?>(null);

        public ISnapshotPersistence ForBackgroundWork() => Background ??= new RecordingSnapshotPersistence("background");
    }

    private sealed class RecordingQueue : ISnapshotWriteQueue
    {
        public List<SnapshotWriteJob> Jobs { get; } = [];
        public bool TryEnqueue(SnapshotWriteJob job) { Jobs.Add(job); return true; }
    }

    private static SnapshotStore<Customer, string> Snapshots(ISnapshotPersistence persistence, ISnapshotWriteQueue? queue, IUnitOfWorkContext unitOfWork)
    {
        var options = new EventStoreOptions();
        options.SetSnapshotsEveryNEvents(2);
        return new SnapshotStore<Customer, string>(persistence, new CountingInnerStore<Customer, string>(_ => null),
            new AggregateFactory<Customer, string>(Customer.CreateNew), Registry, options, queue, unitOfWork);
    }

    [Fact]
    public async Task Background_snapshots_are_enqueued_after_the_commit_and_detached_from_the_request()
    {
        var persistence = new RecordingSnapshotPersistence("request");
        var queue = new RecordingQueue();
        var unitOfWork = new FakeUnitOfWork();
        var store = Snapshots(persistence, queue, unitOfWork);

        await store.SaveAsync(NewCustomer("c1"), TestHost.NewCommandContext());   // 2 events: cadence crossed
        queue.Jobs.Should().BeEmpty("a snapshot of an uncommitted state must not be queued");

        await unitOfWork.CompleteAsync();
        queue.Jobs.Should().ContainSingle();
        await queue.Jobs[0].WriteAsync(CancellationToken.None);

        persistence.Saved.Should().BeEmpty("background work must never use the request-bound persistence");
        persistence.Background!.Saved.Should().ContainSingle().Which.Version.Should().Be(1);
    }

    [Fact]
    public async Task Background_snapshots_are_dropped_when_the_unit_of_work_is_rolled_back()
    {
        var queue = new RecordingQueue();
        var unitOfWork = new FakeUnitOfWork();
        var store = Snapshots(new RecordingSnapshotPersistence("request"), queue, unitOfWork);

        await store.SaveAsync(NewCustomer("c1"), TestHost.NewCommandContext());
        unitOfWork.Discard();

        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Inline_snapshots_are_written_inside_the_unit_of_work()
    {
        // Inline writes belong to the caller's transaction (the relational persistence uses
        // its connection), so they happen immediately, through the request-bound persistence.
        var persistence = new RecordingSnapshotPersistence("request");
        var store = Snapshots(persistence, queue: null, new FakeUnitOfWork());

        await store.SaveAsync(NewCustomer("c1"), TestHost.NewCommandContext());

        persistence.Saved.Should().ContainSingle().Which.Instance.Should().Be("request");
    }
}
