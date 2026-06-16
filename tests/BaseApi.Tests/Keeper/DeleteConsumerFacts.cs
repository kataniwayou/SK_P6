using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 70 / req 8 (D-12): the Keeper DELETE state is delete-only — exactly one single-key DEL of
/// L2[entryId] (ExecutionData) and ZERO orchestrator sends. Drop-on-absent (KeyDeleteAsync no-ops on a
/// missing key — no throw).
/// </summary>
public sealed class DeleteConsumerFacts
{
    private static ConsumeContext<KeeperDelete> Ctx(KeeperDelete m, CancellationToken ct)
    {
        var ctx = Substitute.For<ConsumeContext<KeeperDelete>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    private static KeeperDelete NewDelete() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),   // the single DELETE operand (entryId-only — no index/MessageId)
        };

    [Fact]
    [Trait("Phase", "70")]
    public async Task Delete_single_key_entryId_only_and_sends_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new DeleteConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry());

        var m = NewDelete();

        await consumer.Consume(Ctx(m, ct));

        // req 8: exactly ONE single-key DEL of L2[entryId] — NOT the both-key RedisKey[] overload.
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());

        // req 8: DELETE sends nothing to the orchestrator.
        Assert.Empty(send.Sent);
    }

    [Fact]
    [Trait("Phase", "70")]
    public async Task Delete_absent_key_no_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        // KeyDeleteAsync returns false (no key removed) for an absent key — drop-on-absent (no throw).
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new DeleteConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry());

        var m = NewDelete();

        // No throw on an absent key — the consume completes cleanly; the single-key DEL is still issued once.
        await consumer.Consume(Ctx(m, ct));

        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>());
        Assert.Empty(send.Sent);
    }
}
