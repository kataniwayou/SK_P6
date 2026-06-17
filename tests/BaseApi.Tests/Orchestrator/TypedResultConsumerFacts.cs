using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orchestrator.Configuration;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 71 (D-06/D-07): the <see cref="TypedResultConsumer{TMessage}"/> family reshaped into a thin shell
/// that delegates to <see cref="OrchestratorPrePipeline"/> — routing is still purely by message type via the
/// per-subclass <c>Outcome</c> knob (NO status if/switch). Migrated from the retired L1-only straight-through
/// model to the two-consumer + relocation model:
/// <list type="bullet">
///   <item>each sealed subclass fans out ONLY the successor gated on its own outcome (a Failed-gated
///   successor advances for <see cref="StepFailedConsumer"/> but NOT <see cref="StepCompletedConsumer"/>),
///   preserving CorrelationId/WorkflowId lineage and threading <c>executionId</c> UNCHANGED (REQ-71-11 —
///   equality, NOT regeneration);</item>
///   <item>an L1 miss is a graceful business-ack — no throw, no fan-out;</item>
///   <item>a Keeper-INJECT'd <see cref="StepCompleted"/> is processed byte-indistinguishably from a direct
///   processor completion (same <see cref="StepCompletedConsumer"/>, identical fan-out);</item>
///   <item>a duplicate result re-fans-out idempotently (at-least-once / no-dedup, no collapse).</item>
/// </list>
/// </summary>
public sealed class TypedResultConsumerFacts
{
    // ===== test doubles ==========================================================================

    /// <summary>Captures the fanned-out <see cref="NextStepHandoff"/> messages (the Pre fan-out target).</summary>
    private sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<NextStepHandoff> Handoffs { get; } = [];

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    if (ci.ArgAt<object>(0) is NextStepHandoff h) Handoffs.Add(h);
                    return Task.CompletedTask;
                });
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

    private static IConnectionMultiplexer OutPresentL2(IReadOnlyDictionary<string, string> values)
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => values.TryGetValue(((RedisKey)ci[0]).ToString(), out var v) ? (RedisValue)v : RedisValue.Null);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>()).Returns(true);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    // ===== builders ==============================================================================

    private static StepProjection Step(int entryCondition, Guid processorId, string payload, params Guid[] nextStepIds) =>
        new(EntryCondition: entryCondition, ProcessorId: processorId, Payload: payload, NextStepIds: [.. nextStepIds]);

    private static void SeedWorkflow(
        WorkflowL1Store store, Guid workflowId, IReadOnlyDictionary<Guid, StepProjection> steps) =>
        store.Upsert(workflowId, new WorkflowL1([], "*/5 * * * *", Guid.NewGuid(), steps)
        {
            Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
        });

    private static OrchestratorPrePipeline Pipeline(
        WorkflowL1Store store, IConnectionMultiplexer redis, ISendEndpointProvider send) =>
        new(store, new StepAdvancement(), redis, send,
            Options.Create(new RetryOptions { Limit = 3 }),
            OrchestratorTestStubs.Metrics(),
            NullLogger<OrchestratorPrePipeline>.Instance);

    /// <summary>Builds the matching typed consumer for <paramref name="outcome"/> and feeds it a typed result
    /// of that same outcome — the per-type consumer + message pair routed purely by the subclass Outcome knob.
    /// The fanned-out handoffs (on <paramref name="send"/>) are the only effect asserted.</summary>
    private static Task ConsumeFor(StepOutcome outcome, WorkflowL1Store store, IConnectionMultiplexer redis,
        ISendEndpointProvider send, Guid workflowId, Guid stepId, Guid processorId, Guid messageId,
        Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
    {
        var pipeline = Pipeline(store, redis, send);
        var metrics = OrchestratorTestStubs.Metrics();
        return outcome switch
        {
            StepOutcome.Completed => new StepCompletedConsumer(pipeline, metrics, NullLogger<StepCompleted>.Instance)
                .Consume(Ctx(new StepCompleted(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, messageId, ct)),
            StepOutcome.Failed => new StepFailedConsumer(pipeline, metrics, NullLogger<StepFailed>.Instance)
                .Consume(Ctx(new StepFailed(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, messageId, ct)),
            StepOutcome.Cancelled => new StepCancelledConsumer(pipeline, metrics, NullLogger<StepCancelled>.Instance)
                .Consume(Ctx(new StepCancelled(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, messageId, ct)),
            StepOutcome.Processing => new StepProcessingConsumer(pipeline, metrics, NullLogger<StepProcessing>.Instance)
                .Consume(Ctx(new StepProcessing(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, messageId, ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
    }

    private static ConsumeContext<T> Ctx<T>(T message, Guid messageId, CancellationToken ct) where T : class
    {
        var ctx = Substitute.For<ConsumeContext<T>>();
        ctx.Message.Returns(message);
        ctx.MessageId.Returns(messageId);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    // ----- each subclass fans out via SelectNext with ONLY its own Outcome -------------------------

    [Theory]
    [InlineData(StepOutcome.Completed)]
    [InlineData(StepOutcome.Failed)]
    [InlineData(StepOutcome.Cancelled)]
    [InlineData(StepOutcome.Processing)]
    [Trait("Phase", "71")]
    public async Task TypedResultConsumer_fans_out_via_SelectNext_outcome(StepOutcome outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new CapturingSendProvider();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var matchStepId = Guid.NewGuid();          // gated on THIS outcome — must advance
        var matchProcessorId = Guid.NewGuid();
        const string matchPayload = "{\"go\":true}";
        var otherStepId = Guid.NewGuid();          // gated on a DIFFERENT outcome — must NOT advance
        var otherOutcome = outcome == StepOutcome.Completed ? StepOutcome.Failed : StepOutcome.Completed;

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", matchStepId, otherStepId),
            [matchStepId] = Step((int)outcome, matchProcessorId, matchPayload),
            [otherStepId] = Step((int)otherOutcome, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var entryId = Guid.NewGuid();
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "out-blob" });

        var correlationId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        await ConsumeFor(outcome, store, redis, send, workflowId, completedStepId, Guid.NewGuid(),
            Guid.NewGuid(), correlationId, executionId, entryId, ct);

        // EXACTLY the outcome-matched successor fans out (the other-outcome successor is filtered by
        // SelectNext using THIS subclass's Outcome knob — no status if/switch).
        var handoff = Assert.Single(send.Handoffs);
        Assert.Equal(workflowId, handoff.WorkflowId);             // lineage preserved
        Assert.Equal(correlationId, handoff.CorrelationId);
        Assert.Equal(matchStepId, handoff.StepId);                // the matched successor's ids
        Assert.Equal(matchProcessorId, handoff.ProcessorId);
        Assert.Equal(matchPayload, handoff.Payload);
        // REQ-71-11: executionId threaded UNCHANGED (equality, NOT regeneration).
        Assert.Equal(executionId, handoff.ExecutionId);
    }

    // ----- cross-outcome isolation: a Failed-gated successor advances for Failed but NOT Completed --

    [Fact]
    [Trait("Phase", "71")]
    public async Task StepCompletedConsumer_does_not_advance_a_Failed_gated_successor()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var failGatedStepId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", failGatedStepId),
            [failGatedStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var entryId = Guid.NewGuid();
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "out-blob" });

        // StepCompletedConsumer (Outcome=Completed) must NOT fan out the Failed-gated successor (terminal trip-end).
        var completedSend = new CapturingSendProvider();
        await ConsumeFor(StepOutcome.Completed, store, redis, completedSend, workflowId, completedStepId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), entryId, ct);
        Assert.Empty(completedSend.Handoffs);

        // StepFailedConsumer (Outcome=Failed) over the SAME L1 DOES fan it out — the only knob is Outcome.
        // Phase 72 / D-09: the Failed result now reads its out: blob too (uniform, branch-free), so it carries
        // the SAME entryId whose out: blob is present (the processor always-writes a terminal out: blob).
        var failedSend = new CapturingSendProvider();
        await ConsumeFor(StepOutcome.Failed, store, redis, failedSend, workflowId, completedStepId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), entryId, ct);
        Assert.Single(failedSend.Handoffs);
    }

    // ----- L1 miss = graceful business-ack (no throw, no fan-out) ----------------------------------

    [Fact]
    [Trait("Phase", "71")]
    public async Task L1_miss_acks_gracefully_no_throw_no_fanout()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new CapturingSendProvider();
        var store = new WorkflowL1Store();   // empty — every (wf,step) is a miss
        var redis = OutPresentL2(new Dictionary<string, string>());

        await ConsumeFor(StepOutcome.Completed, store, redis, send, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ct);

        Assert.Empty(send.Handoffs);
    }

    // ----- ORCH-01: a Keeper-INJECT'd StepCompleted is indistinguishable from a direct one ---------

    [Fact]
    [Trait("Phase", "71")]
    public async Task Injected_StepCompleted_indistinguishable_from_direct()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var completedProcessorId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        const string nextPayload = "{\"next\":true}";

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, completedProcessorId, "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, nextPayload),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var correlationId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var entryId = Guid.NewGuid();   // the SAME entryId/executionId on both — INJECT reconstructs identical ids
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "out-blob" });

        var direct = new StepCompleted(workflowId, completedStepId, completedProcessorId)
        { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId };
        var injected = new StepCompleted(workflowId, completedStepId, completedProcessorId)
        { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId };

        Assert.Equal(direct, injected);   // record value-equality — the byte-indistinguishability premise.

        var directSend = new CapturingSendProvider();
        await new StepCompletedConsumer(Pipeline(store, redis, directSend), OrchestratorTestStubs.Metrics(),
            NullLogger<StepCompleted>.Instance).Consume(Ctx(direct, Guid.NewGuid(), ct));

        var injectedSend = new CapturingSendProvider();
        await new StepCompletedConsumer(Pipeline(store, redis, injectedSend), OrchestratorTestStubs.Metrics(),
            NullLogger<StepCompleted>.Instance).Consume(Ctx(injected, Guid.NewGuid(), ct));

        // Identical fan-out effect: same count, same handoff ids + executionId (threaded unchanged on both).
        var d = Assert.Single(directSend.Handoffs);
        var i = Assert.Single(injectedSend.Handoffs);
        Assert.Equal(d.WorkflowId, i.WorkflowId);
        Assert.Equal(d.StepId, i.StepId);
        Assert.Equal(d.ProcessorId, i.ProcessorId);
        Assert.Equal(d.Payload, i.Payload);
        Assert.Equal(d.CorrelationId, i.CorrelationId);
        Assert.Equal(d.Data, i.Data);
        Assert.Equal(d.ExecutionId, i.ExecutionId);              // both threaded the SAME executionId unchanged
        Assert.Equal(nextStepId, d.StepId);
        Assert.Equal(nextProcessorId, d.ProcessorId);
        Assert.Equal(executionId, d.ExecutionId);
    }

    // ----- RESIL-03: at-least-once / no-collapse on duplicate StepCompleted delivery ---------------

    /// <summary>
    /// The two-consumer path carries NO dedup/idempotency key, so delivering the SAME
    /// <see cref="StepCompleted"/> (identical ids) TWICE into ONE <see cref="StepCompletedConsumer"/> sharing
    /// ONE <see cref="CapturingSendProvider"/> re-fans-out idempotently (Handoffs.Count == 2, no collapse, no
    /// throw, no lost branch). The duplicate-tolerance invariant (T-71-07) preserved under the new path.
    /// </summary>
    [Fact]
    [Trait("Phase", "71")]
    public async Task Duplicate_StepCompleted_refans_out_no_collapse()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var completedProcessorId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        const string nextPayload = "{\"next\":true}";

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, completedProcessorId, "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, nextPayload),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var correlationId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "out-blob" });

        var msg = new StepCompleted(workflowId, completedStepId, completedProcessorId)
        { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId };

        var send = new CapturingSendProvider();
        var consumer = new StepCompletedConsumer(Pipeline(store, redis, send), OrchestratorTestStubs.Metrics(),
            NullLogger<StepCompleted>.Instance);

        // Deliver the SAME message TWICE (broker redelivery / publish-confirm ambiguity at-least-once shape).
        await consumer.Consume(Ctx(msg, Guid.NewGuid(), ct));
        await consumer.Consume(Ctx(msg, Guid.NewGuid(), ct));

        // No collapse: the second identical delivery is NOT deduped — the fan-out fires twice, no throw.
        Assert.Equal(2, send.Handoffs.Count);
        Assert.Equal(nextStepId, send.Handoffs[0].StepId);
        Assert.Equal(nextStepId, send.Handoffs[1].StepId);
        Assert.Equal(executionId, send.Handoffs[0].ExecutionId);   // executionId threaded unchanged on both
        Assert.Equal(executionId, send.Handoffs[1].ExecutionId);
    }
}
