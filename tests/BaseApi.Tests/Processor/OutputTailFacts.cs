using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 72 (REQ-1/REQ-2 / D-04/D-06) — the shared <see cref="OutputTail"/>: validate output → write
/// <c>L2[OutputData(messageId)]=data</c> for EVERY terminal outcome (Completed/Failed/Cancelled, NOT
/// Processing) → send the matching <c>Step*</c> by result. Facts:
/// <list type="bullet">
///   <item><b>Completed</b> → ONE OutputData write (non-null jittered TTL) + ONE StepCompleted with
///   <c>EntryId == messageId</c>; returns true.</item>
///   <item><b>Failed</b> → ONE OutputData write (non-null TTL) + ONE StepFailed with
///   <c>EntryId == messageId</c>; returns true (Phase 72 inverts the old skip-write).</item>
///   <item><b>Cancelled</b> → ONE OutputData write (non-null TTL) + ONE StepCancelled with
///   <c>EntryId == messageId</c>; returns true.</item>
///   <item><b>Processing</b> → NO write, ONE StepProcessing with <c>EntryId == Guid.Empty</c> (D-04 —
///   no out: blob for the transient status); returns true.</item>
///   <item><b>Write-exhaust</b> (OutputData write throws) → ONE KeeperInject (carrying the DataResult), NO
///   Step* send, and <c>RunAsync</c> returns false (the round trip ended; the caller must not delete).</item>
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
        var completed = Assert.Single(send.Sent.OfType<StepCompleted>());   // one StepCompleted by result
        Assert.Equal(messageId, completed.EntryId);                // A1 closed (req 7): Completed stamps the output messageId
        Assert.Empty(send.SentKeeper);                             // no keeper on the happy path

        // ONE OutputData write keyed by messageId, carrying the data and a NON-NULL (jittered) TTL (req 11).
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);   // keyed by messageId
        Assert.Equal((RedisValue)"{\"n\":1}", set.Value);                          // data written verbatim
        Assert.True(DispatchTestKit.HasNonNullTtl(set.Args, set.Parameters),
            "expected a non-null jittered TTL on the OutputData write");
    }

    [Fact]
    public async Task Failed_WritesOutputData_WithTtl_AndStampsEntryId_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        var dr = DispatchTestKit.Result(StepOutcome.Failed, "{\"n\":1}", messageId);
        var proceed = await Build(redis, send).RunAsync(dr, deleteEntryId: Guid.NewGuid(), ct);

        Assert.True(proceed);
        var failed = Assert.Single(send.Sent.OfType<StepFailed>());     // still sends the failure by result
        Assert.Equal(messageId, failed.EntryId);                        // REQ-2: real EntryId (= output messageId)
        Assert.Empty(send.SentKeeper);

        // Phase 72 / REQ-1: a Failed result NOW writes the out: blob (one write, keyed by messageId, non-null TTL).
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);
        Assert.Equal((RedisValue)"{\"n\":1}", set.Value);
        Assert.True(DispatchTestKit.HasNonNullTtl(set.Args, set.Parameters),
            "expected a non-null jittered TTL on the Failed OutputData write");
    }

    [Fact]
    public async Task Cancelled_WritesOutputData_WithTtl_AndStampsEntryId_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        var dr = DispatchTestKit.Result(StepOutcome.Cancelled, "{\"n\":1}", messageId);
        var proceed = await Build(redis, send).RunAsync(dr, deleteEntryId: Guid.NewGuid(), ct);

        Assert.True(proceed);
        var cancelled = Assert.Single(send.Sent.OfType<StepCancelled>());   // sends the cancellation by result
        Assert.Equal(messageId, cancelled.EntryId);                         // REQ-2: real EntryId (= output messageId)
        Assert.Empty(send.SentKeeper);

        // Phase 72 / REQ-1: a Cancelled result NOW writes the out: blob.
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);
        Assert.Equal((RedisValue)"{\"n\":1}", set.Value);
        Assert.True(DispatchTestKit.HasNonNullTtl(set.Args, set.Parameters),
            "expected a non-null jittered TTL on the Cancelled OutputData write");
    }

    [Fact]
    public async Task Processing_SkipsWrite_StillSendsStepProcessing_WithEmptyEntryId_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        var dr = DispatchTestKit.Result(StepOutcome.Processing, "{\"n\":1}", messageId);
        var proceed = await Build(redis, send).RunAsync(dr, deleteEntryId: Guid.NewGuid(), ct);

        Assert.True(proceed);
        var processing = Assert.Single(send.Sent.OfType<StepProcessing>());
        Assert.Equal(Guid.Empty, processing.EntryId);              // D-04: Processing keeps Guid.Empty (no out: blob)
        // D-04: NO OutputData write for the transient Processing status (the gate excludes Processing).
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
