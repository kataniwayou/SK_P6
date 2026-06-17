using global::Keeper;
using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// REQ-71-09 (Phase 71): the Orchestrator INJECT state is NextStepHandoff-driven and self-contained. A
/// Completed handoff (non-empty Data) writes L2[data:messageId]=Data (ExecutionData, jittered TTL) → dispatches
/// an EntryStepDispatch to queue:{ProcessorId} (entryId = messageId, envelope MessageId override) → deletes
/// L2[out:DeleteEntryId] (OutputData), STRICTLY in that order. A non-completed handoff (empty Data) skips the
/// write and dispatches with entryId = Guid.Empty; the out: delete still runs. INJECT never reads L1 to
/// recompute successors; executionId is threaded UNCHANGED. The relocate body is INLINE (cross-assembly
/// firewall — no Keeper→Orchestrator reference).
/// </summary>
public sealed class OrchestratorInjectConsumerFacts
{
    private static IOptions<RecoveryOptions> Recovery() =>
        Options.Create(new RecoveryOptions { ExecutionDataTtlSeconds = 300 });

    private static ConsumeContext<OrchestratorInject> Ctx(OrchestratorInject m, CancellationToken ct)
    {
        var ctx = Substitute.For<ConsumeContext<OrchestratorInject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    private static NextStepHandoff NewHandoff(string data) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            Data = data,
        };

    private static OrchestratorInject NewInject(NextStepHandoff h) =>
        new(h.WorkflowId, h.StepId, h.ProcessorId)
        {
            CorrelationId = h.CorrelationId,
            ExecutionId = h.ExecutionId,
            Handoff = h,
            MessageId = Guid.NewGuid(),
            DeleteEntryId = Guid.NewGuid(),
        };

    [Fact]
    [Trait("Phase", "71")]
    public async Task Completed_inject_writes_data_dispatches_and_deletes_in_strict_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        var h = NewHandoff("{\"out\":1}");
        var m = NewInject(h);

        var consumer = new OrchestratorInjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), Recovery());

        await consumer.Consume(Ctx(m, ct));

        // The single captured send is an EntryStepDispatch to queue:{ProcessorId}, entryId = m.MessageId,
        // with the envelope MessageId overridden to m.MessageId (req 9).
        var (uri, msg) = Assert.Single(send.Sent);
        Assert.Equal(new Uri($"queue:{h.ProcessorId:D}"), uri);
        var dispatch = Assert.IsType<EntryStepDispatch>(msg);
        Assert.Equal(m.MessageId, dispatch.EntryId);          // Completed → entryId = messageId
        Assert.Equal(h.ExecutionId, dispatch.ExecutionId);    // executionId threaded unchanged
        Assert.Equal(h.CorrelationId, dispatch.CorrelationId);
        Assert.Equal(h.Payload, dispatch.Payload);
        Assert.Equal(m.MessageId, Assert.Single(send.SentMessageIds));   // envelope override

        // Strict order: write L2[data:messageId] → dispatch → delete L2[out:DeleteEntryId].
        // Received.InOrder spans the Redis substitute, locking write < delete; the dispatch between them is
        // captured by CapturingSendProvider (asserted single above).
        Received.InOrder(() =>
        {
            db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
            db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        });

        await db.Received(1).StringSetAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.MessageId), (RedisValue)h.Data,
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.OutputData(m.DeleteEntryId), Arg.Any<CommandFlags>());
    }

    [Fact]
    [Trait("Phase", "71")]
    public async Task NonCompleted_inject_skips_data_write_and_dispatches_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        var h = NewHandoff("");   // non-completed continuation — no out: blob to relocate
        var m = NewInject(h);

        var consumer = new OrchestratorInjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), Recovery());

        await consumer.Consume(Ctx(m, ct));

        // No data: write for an empty handoff (write gated on non-empty Data).
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());

        // The dispatch (entryId = Guid.Empty) and the out: delete still occur.
        var (_, msg) = Assert.Single(send.Sent);
        var dispatch = Assert.IsType<EntryStepDispatch>(msg);
        Assert.Equal(Guid.Empty, dispatch.EntryId);
        Assert.Equal(m.MessageId, Assert.Single(send.SentMessageIds));
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.OutputData(m.DeleteEntryId), Arg.Any<CommandFlags>());
    }

    [Fact]
    [Trait("Phase", "71")]
    public async Task Inject_never_reads_L1()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        var h = NewHandoff("{\"out\":1}");
        var m = NewInject(h);

        var consumer = new OrchestratorInjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), Recovery());

        await consumer.Consume(Ctx(m, ct));

        // req 9: the handoff is in-hand on the envelope — INJECT must never read input to recompute successors.
        await db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());

        // By construction the consumer has no L1 store dependency: its single public ctor takes exactly the
        // 4 recovery deps (no IWorkflowL1Store / advancement param).
        var ctor = Assert.Single(typeof(OrchestratorInjectConsumer).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Equal(4, paramTypes.Length);
        Assert.Equal(typeof(IConnectionMultiplexer), paramTypes[0]);
        Assert.Equal(typeof(ISendEndpointProvider), paramTypes[1]);
        Assert.Equal(typeof(IOptions<RetryOptions>), paramTypes[2]);
        Assert.Equal(typeof(IOptions<RecoveryOptions>), paramTypes[3]);
    }

    [Fact]
    [Trait("Phase", "71")]
    public async Task ExecutionId_threaded_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        var h = NewHandoff("{\"out\":1}");
        var m = NewInject(h);

        var consumer = new OrchestratorInjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), Recovery());

        await consumer.Consume(Ctx(m, ct));

        var (_, msg) = Assert.Single(send.Sent);
        var dispatch = Assert.IsType<EntryStepDispatch>(msg);
        Assert.Equal(h.ExecutionId, dispatch.ExecutionId);   // threaded byte-unchanged (no regeneration)
    }
}
