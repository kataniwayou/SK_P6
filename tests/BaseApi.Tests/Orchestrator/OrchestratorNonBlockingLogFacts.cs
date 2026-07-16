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
/// Phase 76 (FW-04 / T-76-05) — the orchestrator's NEW causal-edge records (the fan-out edge record in the
/// next-step loop and the terminal-reached record in the true-terminal branch) are emitted on a NON-BLOCKING
/// guarded path: a <see cref="ThrowingLogger{T}"/> whose <c>Log</c> throws on every call must neither fail nor
/// alter the dispatch. Mirrors 76-01's <c>ThrowingLogger&lt;ProcessorPipeline&gt;</c> in
/// <c>Processor/NonBlockingLogFacts.cs</c>:
/// <list type="bullet">
///   <item><b>Fan-out</b>: under the throwing logger a 2-way fan-out still sends BOTH
///   <see cref="NextStepHandoff"/>s and deletes the <c>out:</c> blob — identical to the normal-logger
///   scaffold — and <see cref="OrchestratorPrePipeline.RunAsync"/> surfaces NO exception.</item>
///   <item><b>Terminal</b>: under the throwing logger the true-terminal branch still acks (RunAsync completes)
///   with NO surfaced exception and NO fan-out/keeper.</item>
/// </list>
/// </summary>
public sealed class OrchestratorNonBlockingLogFacts
{
    /// <summary>An <see cref="ILogger{T}"/> whose every <c>Log</c> call throws — the adversarial exporter/logger
    /// the FW-04 swallow-guard must absorb without failing or altering the fan-out send loop / terminal ack.</summary>
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("adversarial logger: Log always throws (FW-04)");

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // ===== minimal harness (self-contained mirror of OrchestratorPrePipelineFacts) ================

    private sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<NextStepHandoff> Handoffs { get; } = [];
        public List<IKeeperRecoverable> SentKeeper { get; } = [];

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(ci => { Record(ci.ArgAt<object>(0)); return Task.CompletedTask; });
            endpoint.Send(Arg.Any<object>(), Arg.Any<IPipe<SendContext>>(), Arg.Any<CancellationToken>())
                .Returns(ci => { Record(ci.ArgAt<object>(0)); return Task.CompletedTask; });
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

    private static OrchestratorMetrics NewMetrics()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new OrchestratorMetrics(provider.GetRequiredService<IMeterFactory>());
    }

    private static OrchestratorPrePipeline Build(
        WorkflowL1Store store, IConnectionMultiplexer redis, ISendEndpointProvider send,
        ILogger<OrchestratorPrePipeline> logger) =>
        new(store, new StepAdvancement(), redis, send,
            Options.Create(new RetryOptions { Limit = 3 }),
            NewMetrics(), logger);

    private static StepCompleted Completed(Guid workflowId, Guid stepId, Guid entryId) =>
        new(workflowId, stepId, Guid.NewGuid())
        { CorrelationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), EntryId = entryId };

    // ===== facts =================================================================================

    [Fact]
    public async Task OrchestratorPrePipeline_NonBlocking_FanOut_all_sends_survive_a_throwing_logger()
    {
        // FW-04: a 2-way fan-out under a ThrowingLogger — every NextStepHandoff is still sent, the out: blob is
        // still deleted, and RunAsync surfaces NO exception (the guarded edge record's throw is absorbed).
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

        var ex = await Record.ExceptionAsync(() =>
            Build(store, redis, send, new ThrowingLogger<OrchestratorPrePipeline>()).RunAsync(
                Completed(workflowId, completedStepId, entryId), StepOutcome.Completed, Guid.NewGuid(), ct));

        Assert.Null(ex);                                            // (1) no exception surfaces from RunAsync
        Assert.Equal(2, send.Handoffs.Count);                       // (2) every NextStepHandoff still sent
        Assert.Empty(send.SentKeeper);                              //     no keeper escalation
        await db.Received(1).KeyDeleteAsync(                        //     out: delete still ran
            (RedisKey)L2ProjectionKeys.OutputData(entryId), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task OrchestratorPrePipeline_NonBlocking_Terminal_ack_survives_a_throwing_logger()
    {
        // FW-04: the true-terminal branch under a ThrowingLogger — RunAsync still completes (acks) with NO
        // surfaced exception (both guarded terminal logs absorb the throw), no fan-out, no keeper.
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

        var ex = await Record.ExceptionAsync(() =>
            Build(store, redis, send, new ThrowingLogger<OrchestratorPrePipeline>()).RunAsync(
                Completed(workflowId, terminalStepId, Guid.NewGuid()), StepOutcome.Completed, Guid.NewGuid(), ct));

        Assert.Null(ex);                                            // ack survives — RunAsync completes cleanly
        Assert.Empty(send.Handoffs);                                // terminal: no fan-out
        Assert.Empty(send.SentKeeper);                              // terminal: no keeper
    }
}
