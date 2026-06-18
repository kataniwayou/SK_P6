using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// REQ-71-10 (Phase 71): the Orchestrator DELETE state is delete-only — exactly one single-key DEL of
/// L2[out:entryId] (OutputData — the cross-paired out: namespace) and ZERO orchestrator sends. Drop-on-absent
/// (KeyDeleteAsync no-ops on a missing key — no throw).
/// </summary>
public sealed class OrchestratorDeleteConsumerFacts
{
    private static ConsumeContext<OrchestratorDelete> Ctx(OrchestratorDelete m, CancellationToken ct)
    {
        var ctx = Substitute.For<ConsumeContext<OrchestratorDelete>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    private static OrchestratorDelete NewDelete() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),   // the single DELETE operand — the source out: entryId
        };

    [Fact]
    [Trait("Phase", "71")]
    public async Task Delete_performs_one_out_delete_and_no_send()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new OrchestratorDeleteConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), RecoveryTestKit.Metrics());

        var m = NewDelete();

        await consumer.Consume(Ctx(m, ct));

        // req 10: exactly ONE single-key DEL of L2[out:entryId] — the out: namespace, single-key overload.
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.OutputData(m.EntryId), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());

        // req 10: DELETE sends nothing to the orchestrator.
        Assert.Empty(send.Sent);
    }
}
