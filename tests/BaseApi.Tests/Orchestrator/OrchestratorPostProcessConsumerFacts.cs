using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orchestrator.Configuration;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 71 (REQ-71-06 / D-15) — the orchestrator-side <see cref="OrchestratorPostProcessConsumer"/> +
/// <see cref="RelocateTail"/>: a thin <c>IConsumer&lt;NextStepHandoff&gt;</c> shell that writes
/// <c>L2[data:messageId]=Data</c> on a Completed continuation (write-exhaust → INJECT, no dispatch) and
/// dispatches the next <see cref="EntryStepDispatch"/> with <c>entryId = ctx.MessageId</c> (Completed) or
/// <c>Guid.Empty</c> (non-completed). The namespaces cross-pair with the processor tail: it writes
/// <c>data:</c> (the relocated input the next step reads), never <c>out:</c>. <c>executionId</c> is threaded
/// byte-unchanged (no <c>NewId.NextGuid</c>).
/// </summary>
public sealed class OrchestratorPostProcessConsumerFacts
{
    // ===== test doubles ==========================================================================

    /// <summary>A recording <see cref="IStepDispatcher"/> capturing each DispatchAsync call's (entryId,
    /// executionId, ids) so the entryId-branch + executionId-threading assertions are deterministic.</summary>
    private sealed class RecordingDispatcher : IStepDispatcher
    {
        public sealed record Call(Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload,
            Guid CorrelationId, Guid ExecutionId, Guid EntryId);

        public List<Call> Calls { get; } = [];

        public Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
            Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
        {
            Calls.Add(new Call(workflowId, stepId, processorId, payload, correlationId, executionId, entryId));
            return Task.CompletedTask;
        }
    }

    /// <summary>An <see cref="ISendEndpointProvider"/> recording each boxed keeper message it is asked to Send.</summary>
    private sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<IKeeperRecoverable> SentKeeper { get; } = [];

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    if (ci.ArgAt<object>(0) is IKeeperRecoverable kr) SentKeeper.Add(kr);
                    return Task.CompletedTask;
                });
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

    // ===== Redis muxes ===========================================================================

    private static IConnectionMultiplexer Wrap(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>The data: write SUCCEEDS. The production 3-arg <c>StringSetAsync(key, value, ttl)</c> binds
    /// to the SE.Redis 2.13 Expiration/ValueCondition virtual overload; stub the keepTtl 6-arg overload too
    /// (the bare TimeSpan?/When shorthands are extension methods NSubstitute cannot intercept).</summary>
    private static IConnectionMultiplexer WriteOkL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(),
                Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .Returns(true);
        db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);
        return Wrap(db);
    }

    /// <summary>All recorded StringSetAsync calls on the fake db, overload-agnostic (inspect by method
    /// name — the production 3-arg write may bind to either virtual overload).</summary>
    private static IReadOnlyList<(RedisKey Key, RedisValue Value, bool HasTtl)> ReceivedStringSets(IDatabase db) =>
        db.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync))
            .Select(c =>
            {
                var args = c.GetArguments();
                var parameters = c.GetMethodInfo().GetParameters();
                var tsIdx = Array.FindIndex(parameters, p => p.ParameterType == typeof(TimeSpan?));
                var expIdx = Array.FindIndex(parameters, p => p.ParameterType.Name == "Expiration");
                var hasTtl = tsIdx >= 0 ? ((TimeSpan?)args[tsIdx]).HasValue : expIdx >= 0;
                return ((RedisKey)args[0]!, (RedisValue)args[1]!, hasTtl);
            })
            .ToList();

    /// <summary>The data: write THROWS on every bindable virtual overload → write retry exhausts → INJECT.</summary>
    private static IConnectionMultiplexer WriteFaultL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: data: write unreachable");
        db.When(x => x.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(),
                Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        db.When(x => x.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        return Wrap(db);
    }

    // ===== builders ==============================================================================

    private static IOptions<RetryOptions> Retry(int limit = 3) =>
        Options.Create(new RetryOptions { Limit = limit });

    private static IOptions<OrchestratorOutputOptions> OutOptions(int ttl = 300) =>
        Options.Create(new OrchestratorOutputOptions { OutputDataTtlSeconds = ttl });

    private static OrchestratorPostProcessConsumer Build(
        IConnectionMultiplexer redis, IStepDispatcher dispatcher, ISendEndpointProvider send)
    {
        var tail = new RelocateTail(redis, dispatcher, send, Retry(3), OutOptions(300));
        return new OrchestratorPostProcessConsumer(tail);
    }

    private static NextStepHandoff Handoff(string data, Guid? executionId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"step\":true}")
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId   = executionId ?? Guid.NewGuid(),
            Data          = data,
        };

    private static ConsumeContext<NextStepHandoff> Ctx(NextStepHandoff h, Guid messageId)
    {
        var ctx = Substitute.For<ConsumeContext<NextStepHandoff>>();
        ctx.Message.Returns(h);
        ctx.MessageId.Returns(messageId);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    // ===== facts =================================================================================

    [Fact]
    public async Task Completed_handoff_writes_data_and_dispatches_with_entryId_equal_messageId()
    {
        var messageId = Guid.NewGuid();
        var redis = WriteOkL2(out var db);
        var dispatcher = new RecordingDispatcher();
        var send = new CapturingSendProvider();
        var h = Handoff(data: "relocated-input");

        await Build(redis, dispatcher, send).Consume(Ctx(h, messageId));

        // one data: write keyed by messageId carrying the relocated Data with a (jittered, non-null) TTL.
        var set = Assert.Single(ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.ExecutionData(messageId), set.Key);
        Assert.Equal((RedisValue)"relocated-input", set.Value);
        Assert.True(set.HasTtl);

        // one dispatch whose EntryId == messageId (REQ-71-06).
        var call = Assert.Single(dispatcher.Calls);
        Assert.Equal(messageId, call.EntryId);
        Assert.Equal(h.StepId, call.StepId);
        Assert.Equal(h.ProcessorId, call.ProcessorId);
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task NonCompleted_handoff_skips_write_and_dispatches_with_empty_entryId()
    {
        var messageId = Guid.NewGuid();
        var redis = WriteOkL2(out var db);
        var dispatcher = new RecordingDispatcher();
        var send = new CapturingSendProvider();
        var h = Handoff(data: "");   // non-completed continuation — no out: blob to relocate

        await Build(redis, dispatcher, send).Consume(Ctx(h, messageId));

        // NO data: write at all (overload-agnostic).
        Assert.Empty(ReceivedStringSets(db));

        // dispatch with the source sentinel entryId.
        var call = Assert.Single(dispatcher.Calls);
        Assert.Equal(Guid.Empty, call.EntryId);
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task Write_exhaust_escalates_one_INJECT_and_does_not_dispatch()
    {
        var messageId = Guid.NewGuid();
        var redis = WriteFaultL2(out _);
        var dispatcher = new RecordingDispatcher();
        var send = new CapturingSendProvider();
        var h = Handoff(data: "relocated-input");

        await Build(redis, dispatcher, send).Consume(Ctx(h, messageId));

        // exactly one OrchestratorInject to keeper-recovery, carrying the handoff + the data: write key.
        var inject = Assert.Single(send.SentKeeper.OfType<OrchestratorInject>());
        Assert.Equal(messageId, inject.MessageId);
        Assert.Equal(h, inject.Handoff);
        Assert.Equal(Guid.Empty, inject.DeleteEntryId);   // Post has no source out: entry to delete

        // and ZERO dispatches (the INJECT ended the round trip before dispatch).
        Assert.Empty(dispatcher.Calls);
    }

    [Fact]
    public async Task ExecutionId_threaded_unchanged()
    {
        var messageId = Guid.NewGuid();
        var inboundExecutionId = Guid.NewGuid();
        var redis = WriteOkL2(out _);
        var dispatcher = new RecordingDispatcher();
        var send = new CapturingSendProvider();
        var h = Handoff(data: "relocated-input", executionId: inboundExecutionId);

        await Build(redis, dispatcher, send).Consume(Ctx(h, messageId));

        // the dispatched executionId EQUALS the handoff's (threaded unchanged — NOT regenerated).
        var call = Assert.Single(dispatcher.Calls);
        Assert.Equal(inboundExecutionId, call.ExecutionId);
    }
}
