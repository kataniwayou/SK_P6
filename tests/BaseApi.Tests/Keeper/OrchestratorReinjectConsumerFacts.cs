using System.Diagnostics.Metrics;
using global::Keeper.Observability;
using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// REQ-71-08 (Phase 71): the Orchestrator REINJECT state reads L2[out:entryId] via STRLEN; present →
/// re-injects the reconstructed Step-result (NOT a dispatch) to queue:orchestrator-result with the outbound
/// envelope MessageId overridden to the carried m.MessageId; absent/empty (STRLEN==0, no Redis exception) →
/// BY-DESIGN silent drop (no throw, no send) + the shared reinject-dropped counter; a Redis EXCEPTION on the
/// read escalates (Guard exhaustion → throw), NOT a drop.
/// </summary>
public sealed class OrchestratorReinjectConsumerFacts
{
    private static ConsumeContext<OrchestratorReinject> Ctx(OrchestratorReinject m, CancellationToken ct)
    {
        var ctx = Substitute.For<ConsumeContext<OrchestratorReinject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    private static OrchestratorReinject NewReinject(StepOutcome outcome = StepOutcome.Completed) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Outcome = outcome,
        };

    [Fact]
    [Trait("Phase", "71")]
    public async Task Present_out_blob_reinjects_result_with_same_messageId()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = NewReinject(StepOutcome.Completed);
        var db = RecoveryTestKit.Db();
        // STRLEN > 0 → the out: blob is present (the orchestrator reads out:, not data:).
        db.StringLengthAsync(L2ProjectionKeys.OutputData(m.EntryId), Arg.Any<CommandFlags>())
            .Returns(12L);
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new OrchestratorReinjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(),
            RecoveryTestKit.Metrics(), NullLogger<OrchestratorReinjectConsumer>.Instance);

        await consumer.Consume(Ctx(m, ct));

        // Exactly one send: a StepCompleted to queue:orchestrator-result (a RESULT, not a dispatch).
        var (uri, msg) = Assert.Single(send.Sent);
        Assert.Equal(new Uri($"queue:{OrchestratorQueues.Result}"), uri);
        var completed = Assert.IsType<StepCompleted>(msg);
        Assert.Equal(m.WorkflowId, completed.WorkflowId);
        Assert.Equal(m.StepId, completed.StepId);
        Assert.Equal(m.ProcessorId, completed.ProcessorId);
        Assert.Equal(m.CorrelationId, completed.CorrelationId);
        Assert.Equal(m.ExecutionId, completed.ExecutionId);
        Assert.Equal(m.EntryId, completed.EntryId);   // A1: Completed carries EntryId = m.EntryId (the real out: key)

        // req 8: the outbound envelope MessageId is overridden to the carried m.MessageId.
        Assert.Equal(m.MessageId, Assert.Single(send.SentMessageIds));
    }

    [Fact]
    [Trait("Phase", "71")]
    public async Task Failed_outcome_reinjects_StepFailed_with_diagnostic()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = NewReinject(StepOutcome.Failed) with { ErrorMessage = "boom" };
        var db = RecoveryTestKit.Db();
        db.StringLengthAsync(L2ProjectionKeys.OutputData(m.EntryId), Arg.Any<CommandFlags>())
            .Returns(5L);
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new OrchestratorReinjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(),
            RecoveryTestKit.Metrics(), NullLogger<OrchestratorReinjectConsumer>.Instance);

        await consumer.Consume(Ctx(m, ct));

        var (_, msg) = Assert.Single(send.Sent);
        var failed = Assert.IsType<StepFailed>(msg);
        Assert.Equal("boom", failed.ErrorMessage);
        Assert.Equal(Guid.Empty, failed.EntryId);
        Assert.Equal(m.MessageId, Assert.Single(send.SentMessageIds));
    }

    [Fact]
    [Trait("Phase", "71")]
    public async Task Absent_out_entry_drops_and_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = NewReinject(StepOutcome.Completed);
        // STRLEN==0 (NSubstitute returns default(long)==0 unstubbed) → the absent-OR-empty drop case.
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        var metrics = new KeeperMetrics(meterFactory);

        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KeeperMetrics.MeterName && instrument.Name == "keeper_reinject_dropped")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref dropped, measurement));
        listener.Start();

        var consumer = new OrchestratorReinjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(),
            metrics, NullLogger<OrchestratorReinjectConsumer>.Instance);

        await consumer.Consume(Ctx(m, ct));   // no throw

        Assert.Empty(send.Sent);                            // nothing re-injected when the out: blob is gone
        Assert.Equal(1, Interlocked.Read(ref dropped));     // the shared reinject-dropped counter incremented
    }

    [Fact]
    [Trait("Phase", "71")]
    public async Task Redis_fault_on_read_escalates()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = NewReinject(StepOutcome.Completed);
        var db = RecoveryTestKit.Db();
        // A Redis EXCEPTION on the read is infra → Guard exhaustion → throw (NOT a drop).
        db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<long>>(_ => throw new RedisException("redis down"));
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new OrchestratorReinjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(),
            RecoveryTestKit.Metrics(), NullLogger<OrchestratorReinjectConsumer>.Instance);

        await Assert.ThrowsAsync<RedisException>(() => consumer.Consume(Ctx(m, ct)));
        Assert.Empty(send.Sent);   // a fault escalates, it does not silently re-inject
    }
}
