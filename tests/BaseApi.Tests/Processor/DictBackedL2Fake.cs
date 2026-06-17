using System.Collections.Concurrent;
using NSubstitute;
using StackExchange.Redis;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 73 (D-08) — the STATEFUL dict-backed L2 blob store the hermetic fan-in harness
/// (<see cref="FanInHermeticHarnessFacts"/>) drives the real pipeline against. Structurally a copy of the
/// holder shape in <c>tests/BaseApi.Tests/Keeper/FakeRedis.cs</c> (a <see cref="IConnectionMultiplexer"/> /
/// <see cref="IDatabase"/> pair built in the ctor), but the backing <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// is a GENERAL key→value blob store — a write under a key is visible to EVERY later read of that key across
/// the full multi-hop DAG. That round-trip statefulness is the whole point (the contrast doubles in
/// <c>DispatchTestKit</c> are stateless single-arrival fakes whose constant-true writes are never read back —
/// they cannot carry a value hop-to-hop, so D-08 explicitly rejects them for this harness).
/// <para>
/// Wires EXACTLY the four ops the real pipeline calls against the store:
/// <list type="bullet">
///   <item><c>StringGetAsync</c> — read <c>data:</c> (<c>ProcessorPipeline</c>) and <c>out:</c>
///   (<c>OrchestratorPrePipeline</c>): returns the stored value or <see cref="RedisValue.Null"/>.</item>
///   <item><c>StringSetAsync</c> — the <c>OutputTail</c> write. BOTH real virtual overloads are stubbed
///   (Pitfall 1 / T-70-11: a single-overload stub silently returns NSubstitute's default and false-greens
///   the round-trip — the <c>TimeSpan?</c>/<c>When</c> shorthands are extension methods NSubstitute cannot
///   intercept). TTL is ignored (hermetic, no expiry).</item>
///   <item><c>KeyExistsAsync</c> — the gate (<c>ProcessorPipeline</c>): the key is present iff a value was
///   written under it.</item>
///   <item><c>KeyDeleteAsync</c> — delete <c>data:</c>/<c>out:</c> (<c>ProcessorPipeline</c> /
///   <c>OrchestratorPrePipeline</c>): removes the key, returning whether it was present.</item>
/// </list>
/// </para>
/// The public test-only inspectors (<see cref="TryGet"/> / <see cref="ContainsKey"/> / <see cref="Snapshot"/>)
/// let the harness read persisted <c>out:</c>/<c>data:</c> blob values back out for the value-integrity and
/// fan-in assertions. Backed by NSubstitute (the repo's established mocking library — no new package).
/// </summary>
public sealed class DictBackedL2Fake
{
    /// <summary>The stateful L2 blob store: a write under a key survives for every later read of that key
    /// across the full multi-hop DAG round-trip.</summary>
    private readonly ConcurrentDictionary<RedisKey, RedisValue> _store = new();

    /// <summary>The wrapped <see cref="IConnectionMultiplexer"/> — ctor-inject this into the real pipeline.</summary>
    public IConnectionMultiplexer Multiplexer { get; }

    /// <summary>The single <see cref="IDatabase"/> returned by <c>GetDatabase()</c>.</summary>
    public IDatabase Database { get; }

    public DictBackedL2Fake()
    {
        Database = BuildDatabase();
        Multiplexer = BuildMultiplexer(Database);
    }

    /// <summary>Read a persisted blob back (delegates to <see cref="ConcurrentDictionary{TKey,TValue}.TryGetValue"/>)
    /// so a test can assert against the <c>out:</c>/<c>data:</c> values the pipeline wrote.</summary>
    public bool TryGet(RedisKey key, out RedisValue value) => _store.TryGetValue(key, out value);

    /// <summary>True while a value is persisted under the key (e.g. a terminal <c>out:</c> blob still anchored).</summary>
    public bool ContainsKey(RedisKey key) => _store.ContainsKey(key);

    /// <summary>A read-only view over the persisted store so a test can enumerate / count the surviving blobs.</summary>
    public IReadOnlyDictionary<RedisKey, RedisValue> Snapshot => _store;

    private IDatabase BuildDatabase()
    {
        var db = Substitute.For<IDatabase>();

        // READ (data: ProcessorPipeline.cs:88, out: OrchestratorPrePipeline.cs:109): the stored value or Null.
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => Task.FromResult(_store.TryGetValue((RedisKey)ci[0], out var v) ? v : RedisValue.Null));

        // WRITE (OutputTail.cs:66): the 3-arg StringSetAsync(key, value, ttl) the production tail calls binds to
        // a REAL virtual overload. CRITICAL (Pitfall 1 / T-70-11, DispatchTestKit.cs:114-118): in SE.Redis 2.13
        // the ONLY real virtual IDatabase.StringSetAsync overload is the (RedisKey, RedisValue, Expiration,
        // ValueCondition, CommandFlags) form — the TimeSpan?/When/keepTtl shorthands are EXTENSION methods
        // NSubstitute CANNOT intercept (stubbing them is a no-op that false-greens the round-trip). Stub the two
        // real virtual overloads the 3-arg write can bind to (the 5-arg Expiration/ValueCondition AND the
        // 6-arg keepTtl form, mirroring DispatchTestKit.StubWriteFault :181-188). Both persist _store[key] =
        // value and return true; TTL is ignored (hermetic, no expiry).
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(),
                Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .Returns(ci => { _store[(RedisKey)ci[0]] = (RedisValue)ci[1]; return Task.FromResult(true); });
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(ci => { _store[(RedisKey)ci[0]] = (RedisValue)ci[1]; return Task.FromResult(true); });

        // GATE (ProcessorPipeline.cs:80): the key exists iff a value was written under it.
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => Task.FromResult(_store.ContainsKey((RedisKey)ci[0])));

        // DELETE (data: ProcessorPipeline.cs:171, out: OrchestratorPrePipeline.cs:158): remove + report presence.
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => Task.FromResult(_store.TryRemove((RedisKey)ci[0], out _)));

        return db;
    }

    // Verbatim FakeRedis.cs:208-213 — the established IConnectionMultiplexer wrap.
    private static IConnectionMultiplexer BuildMultiplexer(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }
}
