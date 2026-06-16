using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 70 (req 9) — the author-facing seam helpers on <see cref="BaseProcessor"/>: the typed
/// <c>ProcessAsync</c> returns <see cref="DataResult"/>? (one-or-null), and the framework supplies
/// <c>SpawnToPost</c> (Mode-2 spawn to <c>queue:{id:D}-post</c>) and <c>DeleteEntry</c> (delete L2[entryId]).
/// The two carry the load-bearing ASYMMETRY (D-07/D-08):
/// <list type="bullet">
///   <item><b>SpawnToPost SWALLOWS</b> on send-exhaust — no throw, no keeper send; it fires the framework's
///   spawn-drop hook (log + counter) and lets the scheduler re-fire the whole entry.</item>
///   <item><b>DeleteEntry ESCALATES</b> on delete-exhaust — it fires the framework's DELETE-keeper hook.</item>
/// </list>
/// The seam state (db / sendProvider / retry limit / ids / hooks) is wired by the framework via
/// <c>SetSeamState</c> before the seam runs (here driven directly — <c>InternalsVisibleTo("BaseApi.Tests")</c>).
/// </summary>
public sealed class BaseProcessorSeamFacts
{
    /// <summary>A send provider whose every Send (both the plain and the override overload) THROWS — the
    /// send-exhaust surface for the SpawnToPost-swallow proof.</summary>
    private sealed class SendFaultProvider : ISendEndpointProvider
    {
        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: -post send unreachable");
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw boom);
            // The production override calls the EXTENSION Send(object, Action<SendContext>, ct) → the real
            // virtual Send(object, IPipe<SendContext>, ct). Throw on the REAL method (the extension can't be stubbed).
            endpoint.Send(Arg.Any<object>(), Arg.Any<IPipe<SendContext>>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw boom);
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

    private static DispatchTestKit.FakeProcessor WireSeam(
        DispatchTestKit.FakeProcessor processor, IDatabase db, ISendEndpointProvider send,
        Guid entryId, Action<Guid> onSpawnDropped, Func<Task> escalateDelete)
    {
        processor.SetSeamState(
            db, send, retryLimit: 3,
            entryId: entryId,
            processorId: Guid.NewGuid(),
            messageId: Guid.NewGuid(),
            workflowId: Guid.NewGuid(),
            stepId: Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            onSpawnDropped: onSpawnDropped,
            escalateDelete: escalateDelete);
        return processor;
    }

    [Fact]
    public async Task SpawnToPost_SendExhaust_Swallows_FiresDropHook_NoThrow_NoKeeper()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new SendFaultProvider();
        var db = Substitute.For<IDatabase>();
        var droppedExecIds = new List<Guid>();
        var escalated = false;

        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);
        WireSeam(processor, db, send, Guid.NewGuid(),
            onSpawnDropped: id => droppedExecIds.Add(id),
            escalateDelete: () => { escalated = true; return Task.CompletedTask; });

        var spawnExec = Guid.NewGuid();
        // SWALLOW: a send-exhaust must NOT throw out of SpawnToPost.
        await processor.SpawnToPostAsync(
            processor.NewResultPublic(StepOutcome.Completed, "{\"n\":1}"), spawnExec);

        var dropped = Assert.Single(droppedExecIds);
        Assert.Equal(spawnExec, dropped);          // the drop hook carries the minted spawn execId (T-70-10: id only)
        Assert.False(escalated);                   // a spawn drop NEVER escalates to a keeper
    }

    [Fact]
    public async Task SpawnToPost_Success_DoesNotFireDropHook_AndOverridesEnvelopeMessageId()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new DispatchTestKit.CapturingSendProvider();
        var db = Substitute.For<IDatabase>();
        var dropped = false;

        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);
        WireSeam(processor, db, send, Guid.NewGuid(),
            onSpawnDropped: _ => dropped = true,
            escalateDelete: () => Task.CompletedTask);

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

    [Fact]
    public async Task DeleteEntry_Success_NoEscalation()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new DispatchTestKit.CapturingSendProvider();
        var entryId = Guid.NewGuid();
        var db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // delete succeeds
        var escalated = false;

        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);
        WireSeam(processor, db, send, entryId,
            onSpawnDropped: _ => { },
            escalateDelete: () => { escalated = true; return Task.CompletedTask; });

        await processor.DeleteEntryAsync();

        Assert.False(escalated);   // a successful delete never escalates
    }

    [Fact]
    public async Task DeleteEntry_DeleteExhaust_Escalates_ToDeleteHook()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new DispatchTestKit.CapturingSendProvider();
        var entryId = Guid.NewGuid();
        var db = Substitute.For<IDatabase>();
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: entry delete unreachable");
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>())).Do(_ => throw boom);
        var escalated = false;

        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);
        WireSeam(processor, db, send, entryId,
            onSpawnDropped: _ => { },
            escalateDelete: () => { escalated = true; return Task.CompletedTask; });

        await processor.DeleteEntryAsync();   // exhaust must NOT throw — it escalates instead

        Assert.True(escalated);   // a delete-exhaust escalates to the DELETE keeper hook
    }
}
