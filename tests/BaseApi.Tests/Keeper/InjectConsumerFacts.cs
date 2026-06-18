using global::Keeper;
using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 70 / req 7 (D-13): the Keeper INJECT state is DataResult-driven and self-contained. A Completed
/// DataResult writes L2[messageId]=data (OutputData, jittered TTL) → sends the matching Step* with the
/// envelope MessageId overridden to dr.MessageId → deletes L2[DeleteEntryId], STRICTLY in that order
/// (Received.InOrder locks write &lt; delete; Pitfall 5). A non-Completed result skips the write but still
/// sends + deletes. INJECT never reads input (no StringGet/StringLength — req 7).
/// </summary>
public sealed class InjectConsumerFacts
{
    private static IOptions<RecoveryOptions> Recovery() =>
        Options.Create(new RecoveryOptions { ExecutionDataTtlSeconds = 300 });

    private static ConsumeContext<KeeperInject> Ctx(KeeperInject m, CancellationToken ct)
    {
        var ctx = Substitute.For<ConsumeContext<KeeperInject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    private static DataResult NewDataResult(StepOutcome result, string data = "{\"out\":1}") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Result = result,
            Data = data,
        };

    private static KeeperInject NewInject(DataResult dr) =>
        new(dr.WorkflowId, dr.StepId, dr.ProcessorId)
        {
            CorrelationId = dr.CorrelationId,
            ExecutionId = dr.ExecutionId,
            DataResult = dr,
            DeleteEntryId = Guid.NewGuid(),
        };

    [Fact]
    [Trait("Phase", "70")]
    public async Task Inject_completed_writes_output_sends_with_messageId_deletes_source_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        var dr = NewDataResult(StepOutcome.Completed);
        var m = NewInject(dr);

        var consumer = new InjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), Recovery(), RecoveryTestKit.Metrics());

        await consumer.Consume(Ctx(m, ct));

        // The single captured send is a StepCompleted, targeting queue:orchestrator-result, with the
        // envelope MessageId overridden to dr.MessageId (req 6/11).
        var (uri, msg) = Assert.Single(send.Sent);
        Assert.Equal(new Uri($"queue:{OrchestratorQueues.Result}"), uri);
        var completed = Assert.IsType<StepCompleted>(msg);
        Assert.Equal(dr.CorrelationId, completed.CorrelationId);
        Assert.Equal(dr.ExecutionId, completed.ExecutionId);
        Assert.Equal(dr.MessageId, completed.EntryId);   // A1 closed - output key on Completed (req 7)
        Assert.Equal(dr.MessageId, Assert.Single(send.SentMessageIds));   // envelope override

        // Strict order: write L2[messageId]=data → send Step* → delete L2[DeleteEntryId] (Pitfall 5).
        // NSubstitute Received.InOrder only spans the Redis substitute's calls, locking write < delete;
        // the send between them is captured by CapturingSendProvider (asserted single above).
        Received.InOrder(() =>
        {
            db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
            db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        });

        await db.Received(1).StringSetAsync(
            (RedisKey)L2ProjectionKeys.OutputData(dr.MessageId), (RedisValue)dr.Data,
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.DeleteEntryId), Arg.Any<CommandFlags>());
    }

    [Fact]
    [Trait("Phase", "70")]
    public async Task Inject_non_completed_skips_output_write_but_still_sends_and_deletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        var dr = NewDataResult(StepOutcome.Failed);
        var m = NewInject(dr);

        var consumer = new InjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), Recovery(), RecoveryTestKit.Metrics());

        await consumer.Consume(Ctx(m, ct));

        // No OutputData write for a non-Completed result (write gated on Completed — A2).
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());

        // The send (StepFailed, with the envelope override) and the source delete still occur.
        var (_, msg) = Assert.Single(send.Sent);
        Assert.IsType<StepFailed>(msg);
        Assert.Equal(dr.MessageId, Assert.Single(send.SentMessageIds));
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.DeleteEntryId), Arg.Any<CommandFlags>());
    }

    [Fact]
    [Trait("Phase", "70")]
    public async Task Inject_never_reads_input_to_recompute()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        var dr = NewDataResult(StepOutcome.Completed);
        var m = NewInject(dr);

        var consumer = new InjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), Recovery(), RecoveryTestKit.Metrics());

        await consumer.Consume(Ctx(m, ct));

        // req 7: the data is in-hand on the envelope — INJECT must never read input to recompute.
        await db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }
}
