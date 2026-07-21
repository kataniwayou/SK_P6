using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 70 (req 9) / Phase 85 (PB-01/PB-03) — the author-facing seam helper on <see cref="BaseProcessor"/>:
/// the typed <c>ProcessAsync</c> returns <see cref="DataResult"/>? (one-or-null), and the framework supplies
/// <c>SpawnToPost</c> (Mode-2 spawn to <c>queue:{id:D}-post</c>).
/// <list type="bullet">
///   <item><b>SpawnToPost FAILS LOUD</b> on a TRANSIENT send-exhaust (PB-01) — it fires the framework's
///   spawn-drop hook (log + counter) FIRST, THEN throws <see cref="SpawnSendExhaustedException"/> so the
///   exhaust propagates → nack-requeue; a deterministic fault surfaces raw.</item>
///   <item><b>Entry deletion is FRAMEWORK-OWNED</b> (PB-03) — it moved off the author seam into the pipeline
///   null-return tail, so there is no author <c>DeleteEntry</c> helper here anymore; delete + DELETE-keeper
///   escalation coverage lives in <c>PrePipelineFacts</c>.</item>
/// </list>
/// The seam state (db / sendProvider / retry limit / ids / hook) is wired by the framework via
/// <c>SetSeamState</c> before the seam runs (here driven directly — <c>InternalsVisibleTo("BaseApi.Tests")</c>).
/// </summary>
public sealed class BaseProcessorSeamFacts
{
    private static DispatchTestKit.FakeProcessor WireSeam(
        DispatchTestKit.FakeProcessor processor, IDatabase db, ISendEndpointProvider send,
        Action<Guid> onSpawnDropped)
    {
        processor.SetSeamState(
            db, send, retryLimit: 3,
            processorId: Guid.NewGuid(),
            messageId: Guid.NewGuid(),
            workflowId: Guid.NewGuid(),
            stepId: Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            onSpawnDropped: onSpawnDropped);
        return processor;
    }

    [Fact]
    public async Task SpawnToPost_SendExhaust_FiresDropHook_ThenThrows_NoKeeper()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new DispatchTestKit.SendFaultProvider();   // transient RedisConnectionException ∈ IsTransientSendFault
        var db = Substitute.For<IDatabase>();
        var droppedExecIds = new List<Guid>();

        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);
        WireSeam(processor, db, send,
            onSpawnDropped: id => droppedExecIds.Add(id));

        var spawnExec = Guid.NewGuid();
        // PB-01/D-01: a TRANSIENT send-exhaust must FAIL LOUD — throw the dedicated escape signal.
        var ex = await Assert.ThrowsAsync<SpawnSendExhaustedException>(() =>
            processor.SpawnToPostAsync(
                processor.NewResultPublic(StepOutcome.Completed, "{\"n\":1}"), spawnExec));

        var dropped = Assert.Single(droppedExecIds);
        Assert.Equal(spawnExec, dropped);          // SC-1: the drop hook fired FIRST, carrying the minted spawn execId (T-70-10: id only)
        Assert.Equal(spawnExec, ex.ExecutionId);   // the exception carries the same spawn execId (ids-only)
    }

    [Fact]
    public async Task SpawnToPost_DeterministicSendFault_Throws_RawException_NotDedicatedType()
    {
        var ct = TestContext.Current.CancellationToken;
        // A NON-transient boom (∉ IsTransientSendFault) — must surface RAW, not the dedicated type (D-03 preserved).
        var send = new DispatchTestKit.SendFaultProvider(new ArgumentException("stub: deterministic -post send fault"));
        var db = Substitute.For<IDatabase>();
        var droppedExecIds = new List<Guid>();

        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);
        WireSeam(processor, db, send,
            onSpawnDropped: id => droppedExecIds.Add(id));

        // The deterministic fault surfaces as the RAW ArgumentException — NOT SpawnSendExhaustedException.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            processor.SpawnToPostAsync(
                processor.NewResultPublic(StepOutcome.Completed, "{\"n\":1}"), Guid.NewGuid()));

        Assert.Empty(droppedExecIds);   // a deterministic fault does NOT fire the transient drop telemetry
    }

    [Fact]
    public async Task SpawnToPost_Success_DoesNotFireDropHook_AndOverridesEnvelopeMessageId()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new DispatchTestKit.CapturingSendProvider();
        var db = Substitute.For<IDatabase>();
        var dropped = false;

        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);
        WireSeam(processor, db, send,
            onSpawnDropped: _ => dropped = true);

        var result = processor.NewResultPublic(StepOutcome.Completed, "{\"n\":2}");
        await processor.SpawnToPostAsync(result, Guid.NewGuid());

        Assert.False(dropped);                                   // a successful spawn does not drop
        var spawned = Assert.Single(send.SentData);              // exactly one DataResult to -post
        // req 11: the outbound envelope MessageId was overridden to the carried result MessageId.
        var envMsgId = Assert.Single(send.SentMessageIds);
        Assert.Equal(result.MessageId, envMsgId);
        Assert.Equal(result.MessageId, spawned.MessageId);
        // routed to queue:{processorId:D}-post
        var toUri = Assert.Single(send.SentDataToUri);
        Assert.EndsWith("-post", toUri.Uri.ToString());
    }

    /// <summary>CR-01 regression: two consumes interleave on ONE shared (Singleton-modelled)
    /// <see cref="DispatchTestKit.FakeProcessor"/> with DIFFERENT messageId. Each consume runs on its own async
    /// flow: it sets its seam state, yields (letting the sibling set ITS state — the clobber window), then reads
    /// back via NewResult. With the old shared-instance-field design the second SetSeamState overwrote the first
    /// consume's ids, so consume A stamped B's messageId — this fact FAILS against that design. With the
    /// AsyncLocal seam state each flow sees only its own ids, so no cross-message bleed. (PB-03: the entry-delete
    /// half of this regression moved to the framework null-path — the messageId-stamp isolation still fully
    /// exercises the per-dispatch AsyncLocal via <c>NewResult</c>.)</summary>
    [Fact]
    public async Task ConcurrentConsumes_DoNotBleed_SeamStateIsPerDispatch()
    {
        var send = new DispatchTestKit.CapturingSendProvider();
        var db = Substitute.For<IDatabase>();

        // ONE shared processor instance — the Singleton the author registers.
        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);

        // A barrier so BOTH consumes have set their seam state BEFORE either reads it (max clobber window).
        var bothSet = new System.Threading.Barrier(2);

        async Task<(Guid tag, Guid stampedMessageId)> Consume(Guid tag, Guid messageId)
        {
            await Task.Yield();   // ensure an independent async flow per consume
            processor.SetSeamState(
                db, send, retryLimit: 3,
                processorId: Guid.NewGuid(),
                messageId: messageId,
                workflowId: Guid.NewGuid(),
                stepId: Guid.NewGuid(),
                correlationId: Guid.NewGuid(),
                onSpawnDropped: _ => { });

            bothSet.SignalAndWait();   // sibling has now clobbered the shared instance IF state were shared

            // Read back through the real helper AFTER the interleave.
            var stamped = processor.NewResultPublic(StepOutcome.Completed, "{\"n\":1}");
            processor.ClearSeamState();
            return (tag, stamped.MessageId);
        }

        var aTag = Guid.NewGuid(); var aMsg = Guid.NewGuid();
        var bTag = Guid.NewGuid(); var bMsg = Guid.NewGuid();

        var results = await Task.WhenAll(Consume(aTag, aMsg), Consume(bTag, bMsg));

        // Each consume's NewResult must carry ITS OWN messageId (no stamp bleed).
        foreach (var (tag, stampedMessageId) in results)
        {
            var expected = tag == aTag ? aMsg : bMsg;
            Assert.Equal(expected, stampedMessageId);
        }
    }
}
