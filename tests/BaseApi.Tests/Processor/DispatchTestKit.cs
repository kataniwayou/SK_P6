using System.Diagnostics.Metrics;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Shared kit for the Phase-70 two-consumer <see cref="ProcessorPipeline"/> / <see cref="OutputTail"/> /
/// <see cref="PostProcessConsumer"/> facts (D-17): a configurable fake <see cref="BaseProcessorBase"/> (the
/// Pre seam) returning a <see cref="DataResult"/>? (one-or-null) OR throwing, a
/// <see cref="CapturingSendProvider"/> capturing BOTH <see cref="IStepResult"/> (orchestrator results) AND
/// <see cref="IKeeperRecoverable"/> (Keeper-state) sends — plus the envelope <see cref="SendContext.MessageId"/>
/// set by the override-overload lambda (<see cref="CapturingSendProvider.SentMessageIds"/>, req 6/11) — the
/// <see cref="RetryOptions"/>/<see cref="ProcessorLivenessOptions"/> option helpers, and a family of
/// Redis-multiplexer fakes covering the linear-Pre / OutputTail fault surfaces: <see cref="GateFaultL2"/>
/// (existence-check fault → REINJECT), <see cref="CleanAbsentL2"/> (clean-absent → no processing),
/// <see cref="ReadFaultL2"/> / <see cref="AbsentReadL2"/> (read fault → REINJECT), <see cref="ReadWriteDeleteOkL2"/>
/// (happy completed write + entry delete), <see cref="OutputWriteFaultL2"/> (output-write fault → INJECT),
/// and <see cref="ReadOkDeleteFaultL2"/> (entry-delete-exhaust → DELETE).
/// </summary>
internal static class DispatchTestKit
{
    /// <summary>A trivial field-less author config for the fake — <c>{"cfg":1}</c> deserializes harmlessly
    /// into it under the framework's ignore-unknown options, keeping every pipeline-double fact deser-inert.</summary>
    public sealed record DummyConfig : ProcessorConfig;

    /// <summary>
    /// A test-double <see cref="BaseProcessor{DummyConfig}"/> whose typed Pre <c>ProcessAsync</c> seam either
    /// returns a configurable <see cref="DataResult"/>? (recording the validatedData/executionId it was called
    /// with), throws (the throw ctor serves BOTH the <c>ProcessStatusException</c> family AND the
    /// unexpected-exception case), or runs a caller-supplied delegate that may call <c>this.SpawnToPost</c>
    /// and return null (a Mode-2 fake). PB-03: entry deletion is framework-owned, so the double exposes no
    /// delete helper. The framework deserializes the dispatch payload
    /// into a <see cref="DummyConfig"/> before invoking the seam — the double is deserialize-inert (no field).
    /// </summary>
    public sealed class FakeProcessor : BaseProcessor<DummyConfig>
    {
        private readonly Func<byte[], DummyConfig?, Guid, CancellationToken, Task<DataResult?>> _impl;

        /// <summary>Returns the given <see cref="DataResult"/>? (or null) from the seam.</summary>
        public FakeProcessor(DataResult? toReturn)
            => _impl = (validatedData, config, executionId, _) =>
            {
                Invoked = true;
                LastInputData = validatedData;
                LastExecutionId = executionId;
                return Task.FromResult(toReturn);
            };

        /// <summary>The seam throws the given exception (the <c>ProcessStatusException</c> family + unexpected).</summary>
        public FakeProcessor(Exception toThrow)
            => _impl = (validatedData, config, executionId, _) =>
            {
                Invoked = true;
                LastInputData = validatedData;
                LastExecutionId = executionId;
                throw toThrow;
            };

        /// <summary>A Mode-2 fake: the delegate receives THIS processor so it can call <c>SpawnToPost</c> /
        /// <c>NewResult</c> (the framework wired the seam state before the call) and return a
        /// <see cref="DataResult"/>? (typically null after a spawn). PB-03: the entry delete is framework-owned
        /// (pipeline null-path tail) — the delegate no longer deletes.</summary>
        public FakeProcessor(Func<FakeProcessor, Guid, Task<DataResult?>> impl)
            => _impl = (validatedData, config, executionId, ct) =>
            {
                Invoked = true;
                LastInputData = validatedData;
                LastExecutionId = executionId;
                return impl(this, executionId);
            };

        /// <summary>True once the transform was actually invoked (proves the Pre guards short-circuited or not).</summary>
        public bool Invoked { get; private set; }
        public byte[]? LastInputData { get; private set; }
        /// <summary>The inbound per-instance executionId the seam threaded in (Guid.Empty == entry/seed).</summary>
        public Guid LastExecutionId { get; private set; }

        /// <summary>Author-callable wrapper over the protected <c>SpawnToPost</c> so a Mode-2 fact delegate can
        /// spawn to the <c>-post</c> queue through the framework-wired seam state.</summary>
        public Task SpawnToPostAsync(DataResult result, Guid executionId) => SpawnToPost(result, executionId);

        /// <summary>Author-callable wrapper over the protected <c>NewResult</c> factory.</summary>
        public DataResult NewResultPublic(StepOutcome outcome, string data) => NewResult(outcome, data);

        protected override Task<DataResult?> ProcessAsync(
            byte[] validatedData, DummyConfig? config, Guid executionId, CancellationToken ct)
            => _impl(validatedData, config, executionId, ct);
    }

    /// <summary>Builds a <see cref="DataResult"/> carrying the given outcome + data with stable test ids
    /// (a fresh MessageId unless one is supplied) — the seam-return shape a Pre/Post fact returns.</summary>
    public static DataResult Result(StepOutcome outcome, string data, Guid? messageId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId   = Guid.NewGuid(),
            MessageId     = messageId ?? Guid.NewGuid(),
            Result        = outcome,
            Data          = System.Text.Encoding.UTF8.GetBytes(data),
        };

    // ===== StringSetAsync received-call inspection (overload-agnostic) =====
    // In SE.Redis 2.13 the ONLY virtual IDatabase.StringSetAsync overload is the
    // (RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags) form; the TimeSpan?/keepTtl/When
    // shorthands are EXTENSION methods NSubstitute cannot intercept (using them in Received()/DidNotReceive()
    // leaks argument matchers — RedundantArgumentMatcherException). So inspect ReceivedCalls() by method name.

    /// <summary>All recorded StringSetAsync calls on the fake db (overload-agnostic).</summary>
    public static IReadOnlyList<(RedisKey Key, RedisValue Value, object?[] Args, System.Reflection.ParameterInfo[] Parameters)>
        ReceivedStringSets(IDatabase db) =>
        db.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync))
            .Select(c =>
            {
                var args = c.GetArguments();
                return ((RedisKey)args[0]!, (RedisValue)args[1]!, args, c.GetMethodInfo().GetParameters());
            })
            .ToList();

    /// <summary>True if any recorded StringSetAsync call carried a non-null TTL (TimeSpan? or Expiration).</summary>
    public static bool HasNonNullTtl(object?[] args, System.Reflection.ParameterInfo[] parameters)
    {
        var tsIdx = Array.FindIndex(parameters, p => p.ParameterType == typeof(TimeSpan?));
        if (tsIdx >= 0) return ((TimeSpan?)args[tsIdx]).HasValue;
        var expIdx = Array.FindIndex(parameters, p => p.ParameterType.Name == "Expiration");
        return expIdx >= 0;   // an Expiration arg is always a concrete (non-null) relative expiry here
    }

    // ===== Redis multiplexer fakes (linear Pre flow + OutputTail fault surfaces) =====

    private static IConnectionMultiplexer Wrap(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>Resolves registered keys on StringGetAsync (absent → RedisValue.Null).</summary>
    private static void StubReads(IDatabase db, IReadOnlyDictionary<string, string> values) =>
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => values.TryGetValue(((RedisKey)ci[0]).ToString(), out var v) ? (RedisValue)v : RedisValue.Null);

    /// <summary>Stubs the OutputData write to SUCCEED. The production tail calls the 3-arg
    /// <c>StringSetAsync(key, value, ttl)</c>, which binds to the REAL (virtual) keepTtl 6-arg interface
    /// overload (the <c>TimeSpan?</c>/<c>When</c> shorthands are extension methods NSubstitute cannot
    /// intercept — using them here leaks argument matchers, RedundantArgumentMatcherException). Stub ONLY the
    /// real virtual overloads (the 6-arg keepTtl + the SE.Redis 2.13 Expiration/ValueCondition form).</summary>
    private static void StubWriteOk(IDatabase db) =>
        // The real virtual IDatabase.StringSetAsync overload in SE.Redis 2.13 is the keepTtl 6-arg form; the
        // 3-arg write the production tail calls binds to it. (The Expiration/ValueCondition and bare TimeSpan?
        // forms are extension methods — stubbing them leaks NSubstitute matchers.)
        db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);

    /// <summary>Throws <see cref="RedisConnectionException"/> on the REAL (virtual) StringSetAsync overloads
    /// the 3-arg write may bind to (the keepTtl 6-arg AND the 2.13 Expiration/ValueCondition form) — the
    /// OutputData-write fault surface (Pitfall 1/T-70-11: a single-overload stub false-greens). The
    /// <c>TimeSpan?</c>/<c>When</c> shorthands are extension methods and are deliberately NOT stubbed (they
    /// would leak NSubstitute argument matchers).</summary>
    private static void StubWriteFault(IDatabase db, string why)
    {
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, why);
        // The 3-arg write StringSetAsync(key, value, ttl) binds to the Expiration/ValueCondition overload
        // (TimeSpan? implicitly converts to Expiration). Throw on BOTH real virtual overloads it could bind to
        // (Pitfall 1 / T-70-11: a single-overload stub false-greens — an unstubbed Task<bool> returns a
        // completed false and the write looks like it "succeeded").
        db.When(x => x.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(),
                Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        db.When(x => x.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
    }

    /// <summary>
    /// The happy linear-Pre mux: <c>KeyExistsAsync(L2[entryId]) == true</c> (gate open), StringGetAsync
    /// resolves the registered keys, the OutputData write SUCCEEDS, and the entry <c>KeyDeleteAsync</c>
    /// SUCCEEDS — the completed-and-deleted Pre tail. The mock <c>db</c> is returned so a fact can assert
    /// the TTL'd write + the single entry delete.
    /// </summary>
    public static IConnectionMultiplexer ReadWriteDeleteOkL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // gate open
        StubReads(db, values);
        StubWriteOk(db);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        return Wrap(db);
    }

    /// <summary>
    /// The gate-fault mux: <c>KeyExistsAsync</c> (the req-1 existence check) THROWS
    /// <see cref="RedisConnectionException"/> for every key → the gate retry exhausts → <c>KeeperReinject</c>.
    /// The mock <c>db</c> is returned so a fact can assert <c>db.DidNotReceive().KeyDeleteAsync(...)</c>.
    /// </summary>
    public static IConnectionMultiplexer GateFaultL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Redis exists unreachable");
        db.When(x => x.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        db.When(x => x.KeyExistsAsync(Arg.Any<RedisKey>())).Do(_ => throw boom);
        return Wrap(db);
    }

    /// <summary>
    /// The clean-absent mux: <c>KeyExistsAsync(L2[entryId]) == false</c> (req 1 clean-absent) → the Pre flow
    /// returns WITHOUT processing (no read, no seam, no send, no delete).
    /// </summary>
    public static IConnectionMultiplexer CleanAbsentL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        return Wrap(db);
    }

    /// <summary>
    /// The read-fault mux: the gate is open (<c>KeyExistsAsync == true</c>) but <c>StringGetAsync</c> THROWS
    /// <see cref="RedisConnectionException"/> → the read retry exhausts → <c>KeeperReinject</c>. The mock db is
    /// returned so a fact can assert <c>db.DidNotReceive().KeyDeleteAsync(...)</c> (entry left intact).
    /// </summary>
    public static IConnectionMultiplexer ReadFaultL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // gate open
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<RedisValue>>(_ => throw new RedisConnectionException(
                ConnectionFailureType.UnableToConnect, "stub: Redis read unreachable"));
        return Wrap(db);
    }

    /// <summary>
    /// The absent/empty-read mux (A2): the gate is open but <c>StringGetAsync</c> returns
    /// <see cref="RedisValue.Null"/> for EVERY key → the read closure's <c>IsNullOrEmpty</c> guard throws
    /// <c>KeyAbsentException</c>, the loop exhausts, and the Pre routes to <c>KeeperReinject</c>.
    /// <c>KeyDeleteAsync</c> is a no-op success (so a DidNotReceive assertion proves the entry was not deleted).
    /// </summary>
    public static IConnectionMultiplexer AbsentReadL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // gate open
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(_ => RedisValue.Null);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        return Wrap(db);
    }

    /// <summary>
    /// The OutputData-write-fault mux (the INJECT-escalation case): the gate is open, StringGetAsync resolves
    /// registered keys, but the OutputData <c>StringSetAsync</c> THROWS <see cref="RedisConnectionException"/>
    /// across all bindable overloads → the write retry exhausts → <c>KeeperInject</c> (carrying the
    /// self-contained <see cref="DataResult"/>). <c>KeyDeleteAsync</c> is a no-op success (it must NEVER run on
    /// this path — INJECT ends the round trip before the entry delete).
    /// </summary>
    public static IConnectionMultiplexer OutputWriteFaultL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // gate open
        StubReads(db, values);
        StubWriteFault(db, "stub: Redis OutputData write unreachable");
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // no-op (must not run)
        return Wrap(db);
    }

    /// <summary>
    /// The entry-delete-fault mux: the gate is open, StringGetAsync resolves, the OutputData write SUCCEEDS,
    /// but the entry <c>KeyDeleteAsync</c> THROWS <see cref="RedisConnectionException"/> → the delete retry
    /// exhausts → <c>KeeperDelete</c> (delete-only escalation). All single-key delete overloads throw.
    /// </summary>
    public static IConnectionMultiplexer ReadOkDeleteFaultL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // gate open
        StubReads(db, values);
        StubWriteOk(db);
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Redis delete unreachable");
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>())).Do(_ => throw boom);
        return Wrap(db);
    }

    // ===== Options / Retry / Metrics / Dispatch helpers (unchanged) =====

    /// <summary>Options carrying the given execution-data TTL — <see cref="OutputTail"/> applies the jittered
    /// <c>random[ttl, 2×ttl]</c> on the OutputData write (the close-gate redis net-zero invariant).</summary>
    public static IOptions<ProcessorLivenessOptions> Options(int executionDataTtlSeconds) =>
        Microsoft.Extensions.Options.Options.Create(new ProcessorLivenessOptions
        {
            ExecutionDataTtlSeconds = executionDataTtlSeconds,
        });

    /// <summary>The retry budget the pipeline/tail consumes (Limit immediate attempts per L2 op + per send).</summary>
    public static IOptions<RetryOptions> Retry(int limit = 3) =>
        Microsoft.Extensions.Options.Options.Create(new RetryOptions { Limit = limit });

    /// <summary>A real <see cref="ProcessorMetrics"/> for the hermetic facts — built from a live
    /// <see cref="IMeterFactory"/>. No collector is wired, so the increments are no-ops in-test.</summary>
    public static ProcessorMetrics Metrics()
    {
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        return new ProcessorMetrics(meterFactory);
    }

    /// <summary>
    /// An <see cref="EntryStepDispatch"/> with the given correlation + entry id. <paramref name="entryId"/> is
    /// the L2 data key; <see cref="Guid.Empty"/> is the no-input source-step sentinel.
    /// </summary>
    public static EntryStepDispatch Dispatch(Guid entryId, Guid correlationId, string payload = "{\"cfg\":1}") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), payload)
        {
            CorrelationId = correlationId,
            EntryId = entryId,
        };

    /// <summary>
    /// An <see cref="ISendEndpointProvider"/> whose every resolved endpoint records each boxed message it is
    /// asked to <c>Send</c>: an <see cref="IStepResult"/> lands in <see cref="Sent"/> (orchestrator results),
    /// an <see cref="IKeeperRecoverable"/> lands in <see cref="SentKeeper"/> (Keeper-state messages), and a
    /// <see cref="DataResult"/> lands in <see cref="SentData"/> (the -post spawns). Both the plain
    /// <c>Send(object, ct)</c> overload AND the Phase-70 envelope-override overload
    /// <c>Send(object, Action&lt;SendContext&gt;, ct)</c> are captured; for the override overload the callback
    /// is materialized against a substituted <see cref="SendContext"/> so the set <see cref="SendContext.MessageId"/>
    /// is recorded into <see cref="SentMessageIds"/> (req 6/11 — re-emit with the carried messageId). The
    /// endpoint URI each message was sent to is recorded in <see cref="SentUris"/> (parallel to the captures).
    /// </summary>
    public sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<IStepResult> Sent { get; } = new();
        public List<IKeeperRecoverable> SentKeeper { get; } = new();
        public List<DataResult> SentData { get; } = new();
        public List<(Uri Uri, DataResult Message)> SentDataToUri { get; } = new();
        /// <summary>The envelope MessageId set by an override-overload send, in send order.</summary>
        public List<Guid> SentMessageIds { get; } = new();

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();

            // Plain object overload (no envelope override) — Step*/Keeper*/DataResult all route here.
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(ci => { Record(address, ci.ArgAt<object>(0)); return Task.CompletedTask; });

            // Phase-70 envelope override: the production calls the EXTENSION Send(object, Action<SendContext>, ct),
            // which forwards to the REAL virtual ISendEndpoint.Send(object, IPipe<SendContext>, ct). NSubstitute
            // can only intercept the real method (stubbing the extension leaks argument matchers). Materialize
            // the pipe against a real SendContext and record the MessageId the override set (req 6/11).
            endpoint.Send(Arg.Any<object>(), Arg.Any<IPipe<SendContext>>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    var msg = ci.ArgAt<object>(0);
                    var pipe = ci.ArgAt<IPipe<SendContext>>(1);
                    var sendCtx = Substitute.For<SendContext>();
                    await pipe.Send(sendCtx);                    // applies ctx.MessageId = carried id
                    SentMessageIds.Add(sendCtx.MessageId ?? Guid.Empty);
                    Record(address, msg);
                });

            return Task.FromResult(endpoint);
        }

        private void Record(Uri address, object o)
        {
            switch (o)
            {
                case IStepResult sr: Sent.Add(sr); break;
                case IKeeperRecoverable kr: SentKeeper.Add(kr); break;
                case DataResult dr: SentData.Add(dr); SentDataToUri.Add((address, dr)); break;
            }
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

    /// <summary>A send provider whose every resolved endpoint's <c>Send</c> (BOTH the plain object overload AND
    /// the Phase-70 envelope-override <c>Send(object, IPipe&lt;SendContext&gt;, ct)</c> the extension forwards to)
    /// THROWS a configurable <paramref name="boom"/> — the send-exhaust surface for the PB-01 fail-loud proof.
    /// Defaults to a TRANSIENT <see cref="RedisConnectionException"/> (∈ <c>IsTransientSendFault</c>) so the
    /// exhaust lands on the new dedicated-throw branch; pass a NON-transient boom (e.g. an
    /// <see cref="ArgumentException"/>) to exercise the deterministic raw-throw negative control.</summary>
    public sealed class SendFaultProvider(Exception? boom = null) : ISendEndpointProvider
    {
        private readonly Exception _boom =
            boom ?? new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: -post send unreachable");

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw _boom);
            // The production override calls the EXTENSION Send(object, Action<SendContext>, ct) → the real
            // virtual Send(object, IPipe<SendContext>, ct). Throw on the REAL method (the extension can't be stubbed).
            endpoint.Send(Arg.Any<object>(), Arg.Any<IPipe<SendContext>>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw _boom);
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }
}
