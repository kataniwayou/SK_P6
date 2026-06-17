using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orchestrator.Dispatch;
using Orchestrator.L1;
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

    /// <summary>A capturing <see cref="ILogger{T}"/> recording each formatted message so the two DISTINCT
    /// trip-end lines (<c>completed-terminal</c> / <c>completed-unresolved</c>) are assertable.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

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

    private static OrchestratorPrePipeline Build(
        WorkflowL1Store store, IConnectionMultiplexer redis, ISendEndpointProvider send,
        ILogger<OrchestratorPrePipeline>? logger = null) =>
        new(store, new StepAdvancement(), redis, send,
            Options.Create(new RetryOptions { Limit = 3 }),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorPrePipeline>.Instance);

    private static StepCompleted Completed(Guid workflowId, Guid stepId, Guid entryId, Guid? executionId = null) =>
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

        await Build(store, redis, send, logger).RunAsync(
            Completed(workflowId, completedStepId, Guid.NewGuid()), StepOutcome.Completed, Guid.NewGuid(), ct);

        Assert.Contains(logger.Messages, msg => msg.Contains("completed-terminal"));
        Assert.Empty(send.Handoffs);
        Assert.Empty(send.SentKeeper);                            // no keeper, no throw
    }

    [Fact]
    public async Task Total_L1_miss_logs_completed_unresolved_and_acks()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new WorkflowL1Store();   // empty — every (wf,step) is a miss
        var redis = OutPresentL2(new Dictionary<string, string>(), out _);
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();

        await Build(store, redis, send, logger).RunAsync(
            Completed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), StepOutcome.Completed, Guid.NewGuid(), ct);

        Assert.Contains(logger.Messages, msg => msg.Contains("completed-unresolved"));
        Assert.Empty(send.Handoffs);
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task Failed_continuation_writes_no_data_and_dispatches_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var failedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();

        // a Failed-gated successor advances on a Failed outcome — no out: blob is read (non-completed).
        var steps = new Dictionary<Guid, StepProjection>
        {
            [failedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = Seed(workflowId, steps);
        var redis = OutPresentL2(new Dictionary<string, string>(), out var db);
        var send = new CapturingSendProvider();

        var failed = new StepFailed(workflowId, failedStepId, Guid.NewGuid())
        { CorrelationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), EntryId = Guid.Empty };

        await Build(store, redis, send).RunAsync(failed, StepOutcome.Failed, Guid.NewGuid(), ct);

        var handoff = Assert.Single(send.Handoffs);
        Assert.Equal("", handoff.Data);                          // non-completed → no relocated data
        await db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // no out: read
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // no out: delete
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
}
