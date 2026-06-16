using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 70 (D-15 / req 4) — the shared <see cref="OutputTail"/>: validate output → write
/// <c>L2[OutputData(messageId)]=data</c> ONLY when the result is <c>Completed</c> → send the matching
/// <c>Step*</c> by result. Facts:
/// <list type="bullet">
///   <item><b>Completed</b> → ONE OutputData write (carrying a non-null jittered TTL) + ONE StepCompleted;
///   <c>RunAsync</c> returns true.</item>
///   <item><b>Non-completed (Failed)</b> → NO write, still ONE StepFailed; returns true.</item>
///   <item><b>Write-exhaust</b> (OutputData write throws) → ONE KeeperInject (carrying the DataResult), NO
///   StepCompleted send, and <c>RunAsync</c> returns false (the round trip ended; the caller must not delete).</item>
/// </list>
/// </summary>
public sealed class OutputTailFacts
{
    private static OutputTail Build(IConnectionMultiplexer redis, DispatchTestKit.CapturingSendProvider send) =>
        new(redis, new FakeProcessorContext { OutputDefinition = null },
            send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300), DispatchTestKit.Metrics());

    [Fact]
    public async Task Completed_WritesOutputDataOnce_WithTtl_AndSendsStepCompleted_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        var dr = DispatchTestKit.Result(StepOutcome.Completed, "{\"n\":1}", messageId);
        var proceed = await Build(redis, send).RunAsync(dr, deleteEntryId: Guid.NewGuid(), ct);

        Assert.True(proceed);                                       // caller may run its own (entry-delete) tail
        Assert.Single(send.Sent.OfType<StepCompleted>());          // one StepCompleted by result
        Assert.Empty(send.SentKeeper);                             // no keeper on the happy path

        // ONE OutputData write keyed by messageId, carrying the data and a NON-NULL (jittered) TTL (req 11).
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);   // keyed by messageId
        Assert.Equal((RedisValue)"{\"n\":1}", set.Value);                          // data written verbatim
        Assert.True(DispatchTestKit.HasNonNullTtl(set.Args, set.Parameters),
            "expected a non-null jittered TTL on the OutputData write");
    }

    [Fact]
    public async Task NonCompleted_Failed_SkipsWrite_StillSendsStepFailed_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        var dr = DispatchTestKit.Result(StepOutcome.Failed, "{\"n\":1}");
        var proceed = await Build(redis, send).RunAsync(dr, deleteEntryId: Guid.NewGuid(), ct);

        Assert.True(proceed);
        Assert.Single(send.Sent.OfType<StepFailed>());             // still sends the failure by result
        // NO OutputData write on a non-Completed result (write-gated-on-completed).
        Assert.Empty(DispatchTestKit.ReceivedStringSets(db));
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task Completed_WriteExhaust_Injects_NoStepSend_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var deleteEntryId = Guid.NewGuid();
        var redis = DispatchTestKit.OutputWriteFaultL2(new Dictionary<string, string>(), out _);
        var send = new DispatchTestKit.CapturingSendProvider();

        var dr = DispatchTestKit.Result(StepOutcome.Completed, "{\"n\":1}", messageId);
        var proceed = await Build(redis, send).RunAsync(dr, deleteEntryId, ct);

        Assert.False(proceed);                                     // INJECT ended the round trip — no delete
        var inject = Assert.Single(send.SentKeeper.OfType<KeeperInject>());
        Assert.Equal(messageId, inject.DataResult.MessageId);      // INJECT carries the self-contained DataResult
        Assert.Equal(deleteEntryId, inject.DeleteEntryId);         // and the source entryId to reclaim
        Assert.Empty(send.Sent.OfType<StepCompleted>());           // NO Step* send on the write-exhaust path
    }
}
