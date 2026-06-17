using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 70 (D-15/D-16 / req 5) — the <see cref="PostProcessConsumer"/>: a thin <c>IConsumer&lt;DataResult&gt;</c>
/// shell over the shared <see cref="OutputTail"/>. It writes <c>L2[OutputData(messageId)]=data</c> on a
/// Completed result and sends the <c>Step*</c> by result — but it NEVER reads or deletes an entry
/// (<c>data = dr.Data</c>, never recomputed from input; deleteEntryId is <c>Guid.Empty</c>). A write-exhaust
/// escalates to INJECT.
/// </summary>
public sealed class PostProcessConsumerFacts
{
    // WR-02: the Post provenance guard drops a DataResult whose ProcessorId != context.Id. The legitimate
    // facts pass the DataResult's own ProcessorId as the bound processor id so the guard admits them.
    private static PostProcessConsumer Build(
        IConnectionMultiplexer redis, DispatchTestKit.CapturingSendProvider send, Guid? processorId = null)
    {
        var context = new FakeProcessorContext { OutputDefinition = null };
        if (processorId is { } id) context.Id = id;
        var tail = new OutputTail(redis, context, send, DispatchTestKit.Retry(3),
            DispatchTestKit.Options(300), DispatchTestKit.Metrics());
        return new PostProcessConsumer(tail, context, DispatchTestKit.Metrics(), NullLogger<PostProcessConsumer>.Instance);
    }

    private static ConsumeContext<DataResult> Ctx(DataResult dr)
    {
        var ctx = Substitute.For<ConsumeContext<DataResult>>();
        ctx.Message.Returns(dr);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task Completed_WritesOutputData_SendsStepCompleted_AndNeverReadsOrDeletesEntry()
    {
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        var dr = DispatchTestKit.Result(StepOutcome.Completed, "post-output", messageId);
        await Build(redis, send, dr.ProcessorId).Consume(Ctx(dr));

        // write output (data == dr.Data, NOT recomputed) + send StepCompleted
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);
        Assert.Equal((RedisValue)"post-output", set.Value);
        Assert.Single(send.Sent.OfType<StepCompleted>());

        // req 5: NO entry read and NO entry delete — Post is output-only.
        await db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
    }

    [Fact]
    public async Task WriteExhaust_Injects()
    {
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.OutputWriteFaultL2(new Dictionary<string, string>(), out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        var dr = DispatchTestKit.Result(StepOutcome.Completed, "post-output", messageId);
        await Build(redis, send, dr.ProcessorId).Consume(Ctx(dr));

        var inject = Assert.Single(send.SentKeeper.OfType<KeeperInject>());
        Assert.Equal(messageId, inject.DataResult.MessageId);
        Assert.Empty(send.Sent.OfType<StepCompleted>());            // no Step* on the write-exhaust path
        // still never touches an entry.
        await db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    /// <summary>WR-02 regression: a -post DataResult whose ProcessorId is NOT this processor's id is a
    /// forge/poisoning vector (arbitrary blob write + forged Step* for another lineage). The provenance guard
    /// DROPS it — no L2 write and no Step* send.</summary>
    [Fact]
    public async Task ForeignProcessorId_IsDropped_NoWrite_NoSend()
    {
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        var dr = DispatchTestKit.Result(StepOutcome.Completed, "forged-output", messageId);
        // Build with a DIFFERENT bound id than the DataResult carries → guard must drop.
        await Build(redis, send, Guid.NewGuid()).Consume(Ctx(dr));

        Assert.Empty(DispatchTestKit.ReceivedStringSets(db));   // no L2 write
        Assert.Empty(send.Sent);                                 // no Step* emitted
        Assert.Empty(send.SentKeeper);                           // no keeper escalation
    }
}
