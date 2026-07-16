using System.Diagnostics.Metrics;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Observability;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 71 (REQ-71-01..05 / 11 / D-14) — the orchestrator-side linear PRE-PROCESS flow of
/// <see cref="OrchestratorPrePipeline"/> (the mirror of the processor's <c>ProcessorPipeline</c>, namespaces
/// cross-paired: it READS/DELETES <c>out:</c> and fans out a handoff the Post tail writes to <c>data:</c>):
/// <list type="number">
///   <item>REQ-71-01/04/05 — Completed with a present <c>out:</c> blob: one NextStepHandoff carrying the
///   blob as Data + one KeyDeleteAsync(OutputData(entryId)); two matches → two handoffs + one delete.</item>
///   <item>REQ-71-01 — a Redis EXCEPTION on the <c>out:</c> read → exactly one REINJECT, zero fan-out.</item>
///   <item>REQ-71-02 — terminal (empty SelectNext) → a DISTINCT <c>completed-terminal</c> log + ack; a total
///   L1 miss → a DISTINCT <c>completed-unresolved</c> log + ack; neither throws nor escalates a keeper
///   (the trip-end metric counter is DEFERRED — the distinct logs carry no-silent-loss).</item>
///   <item>REQ-71-04 — a non-completed continuation writes no Data (Data == "") and reads no <c>out:</c>.</item>
///   <item>REQ-71-05 — delete-exhaust → one DELETE (after the sends landed); send-exhaust → throw, NO delete.</item>
///   <item>REQ-71-11 — the fanned-out handoff's ExecutionId == the inbound result's (threaded unchanged).</item>
/// </list>
/// </summary>
public sealed class OrchestratorPrePipelineFacts
{
    // ===== test doubles ==========================================================================

    /// <summary>An <see cref="ISendEndpointProvider"/> recording each boxed message + the endpoint URI it was
    /// sent to, plus the envelope MessageId set by an override-overload send (to PROVE no override happens on
    /// the live fan-out — SentMessageIds stays empty for the plain-overload Post sends).</summary>
    private sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<NextStepHandoff> Handoffs { get; } = [];
        public List<IKeeperRecoverable> SentKeeper { get; } = [];
        public List<Guid> OverrideMessageIds { get; } = [];
        private readonly Func<Task>? _onSend;

        public CapturingSendProvider(Func<Task>? onSend = null) => _onSend = onSend;

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();

            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    if (_onSend is not null) await _onSend();   // fault-injection hook (send-exhaust)
                    Record(ci.ArgAt<object>(0));
                });

            // the envelope-override overload (used by keeper REINJECT/INJECT/DELETE, NOT the live fan-out):
            endpoint.Send(Arg.Any<object>(), Arg.Any<IPipe<SendContext>>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    var sendCtx = Substitute.For<SendContext>();
                    await ci.ArgAt<IPipe<SendContext>>(1).Send(sendCtx);
                    OverrideMessageIds.Add(sendCtx.MessageId ?? Guid.Empty);
                    Record(ci.ArgAt<object>(0));
                });

            return Task.FromResult(endpoint);
        }

        private void Record(object o)
        {
            switch (o)
            {
                case NextStepHandoff h: Handoffs.Add(h); break;
                case IKeeperRecoverable kr: SentKeeper.Add(kr); break;
            }
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

    /// <summary>Phase 76 (FW-02) — a structured-State capturing <see cref="ILogger{T}"/> (adopted from
    /// <c>Keeper/ReinjectConsumerFacts.cs:24-48</c>) that materializes each entry's message-template State (the
    /// <c>{Placeholder}</c> args MEL exposes as an <see cref="IReadOnlyList{T}"/> of
    /// <see cref="KeyValuePair{TKey,TValue}"/>) so a hermetic fact can assert per-record fields — e.g. the
    /// fan-out edge record's <c>NextStepId</c> distinctness and the terminal-reached record's inbound
    /// <c>EntryId</c>. <see cref="Messages"/> is retained (formatted-string projection) so the existing
    /// DISTINCT trip-end facts (<c>completed-terminal</c> / <c>completed-unresolved</c> / <c>clean-absent</c>)
    /// still assert against the rendered text.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal sealed record Entry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State);

        private readonly List<Entry> _entries = [];
        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>Back-compat projection: the formatted message text of every captured entry.</summary>
        public List<string> Messages => _entries.Select(e => e.Message).ToList();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var pairs = state is IReadOnlyList<KeyValuePair<string, object?>> kvps
                ? kvps.ToList()
                : new List<KeyValuePair<string, object?>>();
            _entries.Add(new Entry(logLevel, formatter(state, exception), pairs));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // ===== FW-02 record filter helpers ============================================================

    /// <summary>The fan-out EDGE records (D-11 Option C): captured entries carrying BOTH a <c>NextStepId</c>
    /// and a <c>CorrelationId</c> placeholder — this excludes the stage-3 "Dangling next-step id" log (which
    /// carries <c>NextStepId</c> but no <c>CorrelationId</c>).</summary>
    private static List<CapturingLogger<OrchestratorPrePipeline>.Entry> EdgeRecords(
        CapturingLogger<OrchestratorPrePipeline> log) =>
        log.Entries.Where(e => e.State.Any(kv => kv.Key == "NextStepId")
                            && e.State.Any(kv => kv.Key == "CorrelationId")).ToList();

    /// <summary>The terminal-reached records (D-12/D-13): captured entries carrying <c>CorrelationId</c> +
    /// <c>EntryId</c> + <c>StepId</c> (and NO <c>NextStepId</c>) — this excludes the existing unstructured
    /// "Trip ended (completed-terminal)" line (no <c>CorrelationId</c>/<c>EntryId</c>) and the fan-out edge
    /// record (carries <c>NextStepId</c>, not <c>StepId</c>).</summary>
    private static List<CapturingLogger<OrchestratorPrePipeline>.Entry> TerminalReachedRecords(
        CapturingLogger<OrchestratorPrePipeline> log) =>
        log.Entries.Where(e => e.State.Any(kv => kv.Key == "CorrelationId")
                            && e.State.Any(kv => kv.Key == "EntryId")
                            && e.State.Any(kv => kv.Key == "StepId")
                            && e.State.All(kv => kv.Key != "NextStepId")).ToList();

    /// <summary>Read a single placeholder value off a captured entry's State (null if absent).</summary>
    private static object? StateValue(CapturingLogger<OrchestratorPrePipeline>.Entry e, string key) =>
        e.State.FirstOrDefault(kv => kv.Key == key).Value;

    // ===== Redis muxes ===========================================================================

    private static IConnectionMultiplexer Wrap(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>StringGetAsync(out:entryId) resolves the registered blob; KeyDeleteAsync succeeds.</summary>
    private static IConnectionMultiplexer OutPresentL2(IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => values.TryGetValue(((RedisKey)ci[0]).ToString(), out var v) ? (RedisValue)v : RedisValue.Null);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>()).Returns(true);
        return Wrap(db);
    }

    /// <summary>StringGetAsync THROWS (Redis fault) → the read retry exhausts → REINJECT.</summary>
    private static IConnectionMultiplexer ReadFaultL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<RedisValue>>(_ => throw new RedisConnectionException(
                ConnectionFailureType.UnableToConnect, "stub: out: read unreachable"));
        return Wrap(db);
    }

    /// <summary>StringGetAsync resolves but KeyDeleteAsync THROWS → the delete retry exhausts → DELETE.</summary>
    private static IConnectionMultiplexer DeleteFaultL2(IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => values.TryGetValue(((RedisKey)ci[0]).ToString(), out var v) ? (RedisValue)v : RedisValue.Null);
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: out: delete unreachable");
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>())).Do(_ => throw boom);
        return Wrap(db);
    }

    // ===== builders ==============================================================================

    private static StepProjection Step(int entryCondition, Guid processorId, string payload, params Guid[] nextStepIds) =>
        new(EntryCondition: entryCondition, ProcessorId: processorId, Payload: payload, NextStepIds: [.. nextStepIds]);

    private static WorkflowL1Store Seed(Guid workflowId, IReadOnlyDictionary<Guid, StepProjection> steps)
    {
        var store = new WorkflowL1Store();
        store.Upsert(workflowId, new WorkflowL1([], "*/5 * * * *", Guid.NewGuid(), steps)
        {
            Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
        });
        return store;
    }

    /// <summary>A real <see cref="OrchestratorMetrics"/> built from a real <see cref="IMeterFactory"/>
    /// (mirror OrchestratorMetricsFacts:26-31) so the in-process <c>Orchestrator</c> meter emits real
    /// measurements a <see cref="MeterCollector"/> can capture.</summary>
    private static OrchestratorMetrics NewMetrics()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new OrchestratorMetrics(provider.GetRequiredService<IMeterFactory>());
    }

    private static OrchestratorPrePipeline Build(
        WorkflowL1Store store, IConnectionMultiplexer redis, ISendEndpointProvider send,
        ILogger<OrchestratorPrePipeline>? logger = null, OrchestratorMetrics? metrics = null) =>
        new(store, new StepAdvancement(), redis, send,
            Options.Create(new RetryOptions { Limit = 3 }),
            metrics ?? NewMetrics(),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorPrePipeline>.Instance);

    private static StepCompleted Completed(Guid workflowId, Guid stepId, Guid entryId, Guid? executionId = null) =>
        new(workflowId, stepId, Guid.NewGuid())
        { CorrelationId = Guid.NewGuid(), ExecutionId = executionId ?? Guid.NewGuid(), EntryId = entryId };

    private static StepFailed Failed(Guid workflowId, Guid stepId, Guid entryId, Guid? executionId = null) =>
        new(workflowId, stepId, Guid.NewGuid())
        { CorrelationId = Guid.NewGuid(), ExecutionId = executionId ?? Guid.NewGuid(), EntryId = entryId };

    private static StepCancelled Cancelled(Guid workflowId, Guid stepId, Guid entryId, Guid? executionId = null) =>
        new(workflowId, stepId, Guid.NewGuid())
        { CorrelationId = Guid.NewGuid(), ExecutionId = executionId ?? Guid.NewGuid(), EntryId = entryId };

    private static StepProcessing Processing(Guid workflowId, Guid stepId, Guid entryId, Guid? executionId = null) =>
        new(workflowId, stepId, Guid.NewGuid())
        { CorrelationId = Guid.NewGuid(), ExecutionId = executionId ?? Guid.NewGuid(), EntryId = entryId };

    // ===== facts =================================================================================

    [Fact]
    public async Task Completed_with_present_out_blob_fans_out_and_deletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, "{\"next\":true}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-output" }, out var db);
        var send = new CapturingSendProvider();

        await Build(store, redis, send).RunAsync(
            Completed(workflowId, completedStepId, entryId), StepOutcome.Completed, Guid.NewGuid(), ct);

        var handoff = Assert.Single(send.Handoffs);
        Assert.Equal(nextStepId, handoff.StepId);
        Assert.Equal(nextProcessorId, handoff.ProcessorId);
        Assert.Equal("the-output", handoff.Data);                 // relocated blob carried inline
        Assert.Empty(send.OverrideMessageIds);                    // D-11: NO envelope override on fan-out
        // exactly one out: delete keyed by entryId, AFTER the send.
        await db.Received(1).KeyDeleteAsync((RedisKey)L2ProjectionKeys.OutputData(entryId), Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task Two_matches_produce_two_post_messages_and_one_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var next1 = Guid.NewGuid();
        var next2 = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", next1, next2),
            [next1] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
            [next2] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-output" }, out var db);
        var send = new CapturingSendProvider();

        await Build(store, redis, send).RunAsync(
            Completed(workflowId, completedStepId, entryId), StepOutcome.Completed, Guid.NewGuid(), ct);

        Assert.Equal(2, send.Handoffs.Count);                     // one Post msg per match
        await db.Received(1).KeyDeleteAsync((RedisKey)L2ProjectionKeys.OutputData(entryId), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Normal_multimatch_fanout_does_not_increment()
    {
        // A fully-resolved multi-match fan-out (every successor resolves, all match) → NO increment.
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var next1 = Guid.NewGuid();
        var next2 = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", next1, next2),
            [next1] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
            [next2] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-output" }, out _);
        var send = new CapturingSendProvider();
        var metrics = NewMetrics();
        using var mc = new MeterCollector(OrchestratorMetrics.MeterName, "orchestrator_step_unresolved");

        await Build(store, redis, send, metrics: metrics).RunAsync(
            Completed(workflowId, completedStepId, entryId), StepOutcome.Completed, Guid.NewGuid(), ct);

        Assert.Equal(2, send.Handoffs.Count);
        Assert.Equal(0, mc.Total);                                // a normal fan-out does NOT increment
    }

    [Fact]
    public async Task Redis_fault_on_read_produces_one_REINJECT_and_no_fanout()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = ReadFaultL2(out var db);
        var send = new CapturingSendProvider();

        await Build(store, redis, send).RunAsync(
            Completed(workflowId, completedStepId, entryId), StepOutcome.Completed, messageId, ct);

        var reinject = Assert.Single(send.SentKeeper.OfType<OrchestratorReinject>());
        Assert.Equal(entryId, reinject.EntryId);
        Assert.Equal(messageId, reinject.MessageId);              // re-asserts the SAME inbound envelope id
        Assert.Equal(StepOutcome.Completed, reinject.Outcome);
        Assert.Empty(send.Handoffs);                              // NO fan-out
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Terminal_step_logs_completed_terminal_and_acks()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var failGatedStepId = Guid.NewGuid();

        // the only successor is Failed-gated → a Completed result has NO match (terminal).
        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", failGatedStepId),
            [failGatedStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(new Dictionary<string, string>(), out _);
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();
        var metrics = NewMetrics();
        using var mc = new MeterCollector(OrchestratorMetrics.MeterName, "orchestrator_step_unresolved");

        await Build(store, redis, send, logger, metrics).RunAsync(
            Completed(workflowId, completedStepId, Guid.NewGuid()), StepOutcome.Completed, Guid.NewGuid(), ct);

        Assert.Contains(logger.Messages, msg => msg.Contains("completed-terminal"));
        Assert.Empty(send.Handoffs);
        Assert.Empty(send.SentKeeper);                            // no keeper, no throw
        Assert.Equal(0, mc.Total);                                // completed-terminal does NOT increment (D-03)
    }

    [Fact]
    public async Task Condition_skip_does_not_increment()
    {
        // A successor resolvable in L1 but with a NON-matching EntryCondition is a deliberate business no-op
        // (D-03) — collected NOWHERE by SelectNext (neither Matches nor UnresolvedIds) → NO increment. With no
        // matches AND no unresolved ids this is a completed-terminal trip-end.
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var failGatedStepId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", failGatedStepId),
            [failGatedStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),   // resolvable, non-matching
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(new Dictionary<string, string>(), out _);
        var send = new CapturingSendProvider();
        var metrics = NewMetrics();
        using var mc = new MeterCollector(OrchestratorMetrics.MeterName, "orchestrator_step_unresolved");

        await Build(store, redis, send, metrics: metrics).RunAsync(
            Completed(workflowId, completedStepId, Guid.NewGuid()), StepOutcome.Completed, Guid.NewGuid(), ct);

        Assert.Empty(send.Handoffs);
        Assert.Equal(0, mc.Total);                                // a condition mismatch is NOT an unresolved miss
    }

    [Fact]
    public async Task Total_L1_miss_logs_completed_unresolved_and_acks()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new WorkflowL1Store();   // empty — every (wf,step) is a miss
        var redis = OutPresentL2(new Dictionary<string, string>(), out _);
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();
        var workflowId = Guid.NewGuid();
        var metrics = NewMetrics();
        using var mc = new MeterCollector(OrchestratorMetrics.MeterName, "orchestrator_step_unresolved");

        await Build(store, redis, send, logger, metrics).RunAsync(
            Completed(workflowId, Guid.NewGuid(), Guid.NewGuid()), StepOutcome.Completed, Guid.NewGuid(), ct);

        Assert.Contains(logger.Messages, msg => msg.Contains("completed-unresolved"));
        Assert.Empty(send.Handoffs);
        Assert.Empty(send.SentKeeper);
        // stage-1: exactly one orchestrator_step_unresolved increment, tagged workflowId ONLY.
        Assert.Equal(1, mc.Total);
        var tag = Assert.Single(mc.Tags[0]);                       // exactly one tag key
        Assert.Equal("workflowId", tag.Key);
        Assert.Equal(workflowId.ToString("D"), tag.Value);
    }

    [Fact]
    public async Task Dangling_next_step_increments_once_and_resolvable_successor_still_fans_out()
    {
        // stage-3 (D-01/D-02/T-72-08): a step whose NextStepIds = [resolvable, dangling]. The resolvable
        // successor still fans out (1 Handoff); the dangling id increments the counter exactly once and is
        // skipped via continue (NEVER throws).
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var resolvableId = Guid.NewGuid();
        var danglingId = Guid.NewGuid();   // deliberately NOT seeded into the L1 step map
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", resolvableId, danglingId),
            [resolvableId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
            // danglingId intentionally absent → SelectNext surfaces it in UnresolvedIds
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-output" }, out _);
        var send = new CapturingSendProvider();
        var metrics = NewMetrics();
        using var mc = new MeterCollector(OrchestratorMetrics.MeterName, "orchestrator_step_unresolved");

        await Build(store, redis, send, metrics: metrics).RunAsync(
            Completed(workflowId, completedStepId, entryId), StepOutcome.Completed, Guid.NewGuid(), ct);

        var resolvableHandoff = Assert.Single(send.Handoffs);      // the resolvable successor still fans out
        Assert.Equal(resolvableId, resolvableHandoff.StepId);
        Assert.Empty(send.SentKeeper);                             // dangling id is a graceful skip, NOT a keeper
        // exactly one increment for the single dangling id, tagged workflowId ONLY.
        Assert.Equal(1, mc.Total);
        var tag = Assert.Single(mc.Tags[0]);
        Assert.Equal("workflowId", tag.Key);
        Assert.Equal(workflowId.ToString("D"), tag.Value);
    }

    [Fact]
    public async Task Failed_with_present_out_blob_fans_out_and_deletes_like_Completed()
    {
        // Phase 72 / REQ-5 / D-09: the read+fan-out+delete now run for a Failed result IDENTICALLY to a
        // Completed one — there is no outcome branch. The processor always-writes the terminal out: blob.
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var failedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [failedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Failed, nextProcessorId, "{\"next\":true}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-fail-output" }, out var db);
        var send = new CapturingSendProvider();

        await Build(store, redis, send).RunAsync(
            Failed(workflowId, failedStepId, entryId), StepOutcome.Failed, Guid.NewGuid(), ct);

        var handoff = Assert.Single(send.Handoffs);
        Assert.Equal(nextStepId, handoff.StepId);
        Assert.Equal("the-fail-output", handoff.Data);                  // relocated blob carried inline (uniform)
        await db.Received(1).StringGetAsync(                            // the out: read ran for a Failed
            (RedisKey)L2ProjectionKeys.OutputData(entryId), Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync(                            // and the out: delete ran for a Failed
            (RedisKey)L2ProjectionKeys.OutputData(entryId), Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task Cancelled_with_present_out_blob_fans_out_and_deletes_like_Completed()
    {
        // D-09: a Cancelled terminal result relocates identically — outcome is consumed ONLY by SelectNext.
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var cancelledStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [cancelledStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Cancelled, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-cancel-output" }, out var db);
        var send = new CapturingSendProvider();

        await Build(store, redis, send).RunAsync(
            Cancelled(workflowId, cancelledStepId, entryId), StepOutcome.Cancelled, Guid.NewGuid(), ct);

        var handoff = Assert.Single(send.Handoffs);
        Assert.Equal("the-cancel-output", handoff.Data);
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.OutputData(entryId), Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task Clean_absent_out_blob_acks_without_fanout_keeper_or_delete()
    {
        // D-10: a cleanly-absent out: blob (empty L2, no Redis fault) → idempotent ack-skip: NO Handoffs,
        // NO keeper, NO delete. A Failed result with a matching successor still rides the skip.
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var failedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [failedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(new Dictionary<string, string>(), out var db);   // empty dict → clean-absent
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();

        await Build(store, redis, send, logger).RunAsync(
            Failed(workflowId, failedStepId, entryId), StepOutcome.Failed, Guid.NewGuid(), ct);

        Assert.Contains(logger.Messages, msg => msg.Contains("clean-absent"));
        Assert.Empty(send.Handoffs);                                                  // NO fan-out
        Assert.Empty(send.SentKeeper);                                                // NO keeper (not a fault)
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // NO delete
    }

    [Fact]
    public async Task Processing_rides_clean_absent_skip_no_fanout()
    {
        // D-05: a Processing result wrote no out: blob → it hits the clean-absent gate and does NOT advance,
        // for free (no special Processing branch). Use a Processing-gated successor so SelectNext matches.
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var procStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [procStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Processing, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(new Dictionary<string, string>(), out var db);   // empty → clean-absent
        var send = new CapturingSendProvider();

        await Build(store, redis, send).RunAsync(
            Processing(workflowId, procStepId, entryId), StepOutcome.Processing, Guid.NewGuid(), ct);

        Assert.Empty(send.Handoffs);                                                  // no advance
        Assert.Empty(send.SentKeeper);
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Delete_exhaust_escalates_one_DELETE()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = DeleteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-output" }, out _);
        var send = new CapturingSendProvider();

        await Build(store, redis, send).RunAsync(
            Completed(workflowId, completedStepId, entryId), StepOutcome.Completed, Guid.NewGuid(), ct);

        // the send landed (fan-out happened) THEN the delete-exhaust escalated exactly one DELETE.
        Assert.Single(send.Handoffs);
        var del = Assert.Single(send.SentKeeper.OfType<OrchestratorDelete>());
        Assert.Equal(entryId, del.EntryId);
    }

    [Fact]
    public async Task Send_exhaust_throws_and_does_not_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-output" }, out var db);
        // every Send throws → the post fan-out send exhausts → RunAsync throws BEFORE the delete.
        var send = new CapturingSendProvider(onSend: () =>
            throw new MassTransitException("stub: post Send fault"));

        await Assert.ThrowsAsync<MassTransitException>(() =>
            Build(store, redis, send).RunAsync(
                Completed(workflowId, completedStepId, entryId), StepOutcome.Completed, Guid.NewGuid(), ct));

        // NO out: delete ran (send-before-delete: the throw short-circuits before the delete).
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ExecutionId_threaded_unchanged_on_continuation()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var inboundExecutionId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-output" }, out _);
        var send = new CapturingSendProvider();

        await Build(store, redis, send).RunAsync(
            Completed(workflowId, completedStepId, entryId, inboundExecutionId), StepOutcome.Completed,
            Guid.NewGuid(), ct);

        var handoff = Assert.Single(send.Handoffs);
        Assert.Equal(inboundExecutionId, handoff.ExecutionId);   // threaded UNCHANGED (REQ-71-11)
    }

    // ===== FW-02 fan-out edge + terminal-reached records (Phase 76, D-11 Option C / D-12 / D-13) =====

    [Fact]
    public async Task FanOut_emits_two_edge_records_with_distinct_NextStepId()
    {
        // FW-02 / D-11 Option C: a 2-way fan-out emits exactly 2 edge records, each carrying the SAME inbound
        // EntryId (M_N) and DISTINCT next StepIds — and NO outbound MessageId (Option C omits it).
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var next1 = Guid.NewGuid();
        var next2 = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", next1, next2),
            [next1] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
            [next2] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-output" }, out _);
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();

        await Build(store, redis, send, logger).RunAsync(
            Completed(workflowId, completedStepId, entryId), StepOutcome.Completed, Guid.NewGuid(), ct);

        var edges = EdgeRecords(logger);
        Assert.Equal(2, edges.Count);                                   // one edge record per next step
        var nextIds = edges.Select(e => (Guid)StateValue(e, "NextStepId")!).ToList();
        Assert.Equal(2, nextIds.Distinct().Count());                    // DISTINCT next StepIds
        Assert.Contains(next1, nextIds);
        Assert.Contains(next2, nextIds);
        // each edge carries the SAME inbound EntryId (M_N) and NO outbound MessageId placeholder.
        Assert.All(edges, e => Assert.Equal(entryId, (Guid)StateValue(e, "EntryId")!));
        Assert.All(edges, e => Assert.DoesNotContain(e.State, kv => kv.Key == "MessageId"));
    }

    [Fact]
    public async Task Terminal_reached_emits_two_records_at_double_fanin_by_distinct_EntryId()
    {
        // FW-02 / D-12 / D-13: a Step_G-shaped double fan-in — the SAME terminal step reached TWICE by two
        // distinct inbound EntryIds — emits exactly 2 terminal-reached records, distinguished by inbound
        // EntryId, on the true-terminal branch only.
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var terminalStepId = Guid.NewGuid();
        var failGatedStepId = Guid.NewGuid();

        // the only successor is Failed-gated → a Completed result has NO match (true terminal).
        var steps = new Dictionary<Guid, StepProjection>
        {
            [terminalStepId] = Step(0, Guid.NewGuid(), "{}", failGatedStepId),
            [failGatedStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(new Dictionary<string, string>(), out _);
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();
        var entryA = Guid.NewGuid();
        var entryB = Guid.NewGuid();

        var pipeline = Build(store, redis, send, logger);
        await pipeline.RunAsync(Completed(workflowId, terminalStepId, entryA), StepOutcome.Completed, Guid.NewGuid(), ct);
        await pipeline.RunAsync(Completed(workflowId, terminalStepId, entryB), StepOutcome.Completed, Guid.NewGuid(), ct);

        var terminals = TerminalReachedRecords(logger);
        Assert.Equal(2, terminals.Count);                               // ×2 at the double fan-in
        var entryIds = terminals.Select(e => (Guid)StateValue(e, "EntryId")!).ToList();
        Assert.Equal(2, entryIds.Distinct().Count());                   // distinguished by inbound EntryId
        Assert.Contains(entryA, entryIds);
        Assert.Contains(entryB, entryIds);
        // terminal-reached carries no outbound MessageId (D-12).
        Assert.All(terminals, e => Assert.DoesNotContain(e.State, kv => kv.Key == "MessageId"));
    }

    [Fact]
    public async Task L1_miss_emits_no_terminal_reached_record()
    {
        // D-12 negative: the completed-unresolved (L1-miss) branch must NOT emit a terminal-reached record.
        var ct = TestContext.Current.CancellationToken;
        var store = new WorkflowL1Store();   // empty — every (wf,step) is a miss
        var redis = OutPresentL2(new Dictionary<string, string>(), out _);
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();

        await Build(store, redis, send, logger).RunAsync(
            Completed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), StepOutcome.Completed, Guid.NewGuid(), ct);

        Assert.Empty(TerminalReachedRecords(logger));                   // L1-miss is NOT "reached terminal"
    }

    [Fact]
    public async Task Clean_absent_emits_no_terminal_reached_record()
    {
        // D-12 negative: the clean-absent out: skip must NOT emit a terminal-reached record.
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var failedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [failedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(new Dictionary<string, string>(), out _);   // empty → clean-absent
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();

        await Build(store, redis, send, logger).RunAsync(
            Failed(workflowId, failedStepId, entryId), StepOutcome.Failed, Guid.NewGuid(), ct);

        Assert.Empty(TerminalReachedRecords(logger));                   // clean-absent is NOT "reached terminal"
    }
}
