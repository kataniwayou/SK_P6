using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 71 (REQ-71-02/05): the result-consume ack split migrated to the two-consumer + relocation model.
/// A typed result consumer now delegates to <see cref="OrchestratorPrePipeline"/>:
/// <list type="bullet">
///   <item>an unknown <c>(workflowId, stepId)</c> result logs a DISTINCT <c>completed-unresolved</c> trip-end
///   line + acks (no throw, no keeper, no fan-out);</item>
///   <item>a completed step with no matching next step logs a DISTINCT <c>completed-terminal</c> line + acks;</item>
///   <item>a Completed result with a matching next step fans out ONE <see cref="NextStepHandoff"/> to
///   orchestrator-result-post carrying the relocated out: blob + the inbound executionId UNCHANGED;</item>
///   <item>an injected infra fault on the broker fan-out <c>Send</c> propagates (does not ack-swallow).</item>
/// </list>
/// The trip-end metric counter is DEFERRED — no-silent-loss rides the two distinct LOG lines + behavior
/// (asserted via a captured logger), not a metric.
/// </summary>
public sealed class ResultAckTests
{
    // ===== test doubles ==========================================================================

    private sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<NextStepHandoff> Handoffs { get; } = [];
        public List<IKeeperRecoverable> SentKeeper { get; } = [];
        private readonly Func<Task>? _onSend;

        public CapturingSendProvider(Func<Task>? onSend = null) => _onSend = onSend;

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    if (_onSend is not null) await _onSend();
                    switch (ci.ArgAt<object>(0))
                    {
                        case NextStepHandoff h: Handoffs.Add(h); break;
                        case IKeeperRecoverable kr: SentKeeper.Add(kr); break;
                    }
                });
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

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
        WorkflowL1Store store, IConnectionMultiplexer redis, ISendEndpointProvider send,
        ILogger<OrchestratorPrePipeline>? logger = null) =>
        new(store, new StepAdvancement(), redis, send,
            Options.Create(new RetryOptions { Limit = 3 }),
            logger ?? NullLogger<OrchestratorPrePipeline>.Instance);

    private static StepCompletedConsumer Build(
        WorkflowL1Store store, IConnectionMultiplexer redis, ISendEndpointProvider send,
        ILogger<OrchestratorPrePipeline>? logger = null) =>
        new(Pipeline(store, redis, send, logger), OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance);

    private static ConsumeContext<StepCompleted> Ctx(StepCompleted message, Guid? messageId = null)
    {
        var ctx = Substitute.For<ConsumeContext<StepCompleted>>();
        ctx.Message.Returns(message);
        ctx.MessageId.Returns(messageId ?? Guid.NewGuid());
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    // ----- unknown (workflowId, stepId) -> distinct completed-unresolved trip-end + ack ----------

    [Fact]
    public async Task UnknownWorkflowStep_LogsCompletedUnresolved_Acks_NoThrow_NoFanout()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();
        var store = new WorkflowL1Store();   // empty — workflow absent

        var consumer = Build(store, OutPresentL2(new Dictionary<string, string>()), send, logger);
        var result = new StepCompleted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) { CorrelationId = Guid.NewGuid() };

        await consumer.Consume(Ctx(result));   // no throw (clean ack)

        Assert.Contains(logger.Messages, m => m.Contains("completed-unresolved"));
        Assert.Empty(send.Handoffs);
        Assert.Empty(send.SentKeeper);
        _ = ct;
    }

    // ----- completed step with NO matching next step -> distinct completed-terminal trip-end + ack

    [Fact]
    public async Task NoMatchingNextStep_LogsCompletedTerminal_Acks_NoThrow_NoFanout()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new CapturingSendProvider();
        var logger = new CapturingLogger<OrchestratorPrePipeline>();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();

        // A Completed result, but the only next step is Failed(2)-gated — no match (terminal).
        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var consumer = Build(store, OutPresentL2(new Dictionary<string, string>()), send, logger);
        var result = new StepCompleted(workflowId, completedStepId, Guid.NewGuid()) { CorrelationId = Guid.NewGuid() };

        await consumer.Consume(Ctx(result));

        Assert.Contains(logger.Messages, m => m.Contains("completed-terminal"));
        Assert.Empty(send.Handoffs);
        Assert.Empty(send.SentKeeper);
        _ = ct;
    }

    // ----- matched next step -> ONE NextStepHandoff carrying the inbound executionId UNCHANGED ----

    [Fact]
    public async Task MatchedNextStep_FansOutOneHandoff_WithExecutionIdUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new CapturingSendProvider();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var resultExecutionId = Guid.NewGuid();   // the inbound instance lineage — must propagate unchanged
        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-blob" });
        var consumer = Build(store, redis, send);
        var result = new StepCompleted(workflowId, completedStepId, Guid.NewGuid())
        { CorrelationId = Guid.NewGuid(), ExecutionId = resultExecutionId, EntryId = entryId };

        await consumer.Consume(Ctx(result));

        // ONE handoff to orchestrator-result-post carrying the relocated blob + the matched successor's ids.
        var handoff = Assert.Single(send.Handoffs);
        Assert.Equal(workflowId, handoff.WorkflowId);
        Assert.Equal(nextStepId, handoff.StepId);
        Assert.Equal(nextProcessorId, handoff.ProcessorId);
        Assert.Equal("the-blob", handoff.Data);
        Assert.Equal(resultExecutionId, handoff.ExecutionId);    // propagated UNCHANGED (REQ-71-11)
        _ = ct;
    }

    // ----- injected infra fault on the fan-out Send propagates (does not ack-swallow) ------------

    [Fact]
    public async Task InfraFaultOnSend_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new CapturingSendProvider(onSend: () => throw new MassTransitException("stub: broker Send fault"));

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var redis = OutPresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.OutputData(entryId)] = "the-blob" });
        var consumer = Build(store, redis, send);
        var result = new StepCompleted(workflowId, completedStepId, Guid.NewGuid())
        { CorrelationId = Guid.NewGuid(), EntryId = entryId };

        // Infra fault must NOT be ack-swallowed — it propagates so the bounded retry -> broker redelivery.
        await Assert.ThrowsAsync<MassTransitException>(() => consumer.Consume(Ctx(result)));
        _ = ct;
    }
}
