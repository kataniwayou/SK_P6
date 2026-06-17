using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orchestrator.Configuration;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 71 (REQ-71-06/11): the Post hop of the two-consumer continuation path, exercised END-TO-END through
/// a real in-memory MassTransit harness — a <see cref="NextStepHandoff"/> sent to
/// <c>orchestrator-result-post</c> is consumed by the <see cref="OrchestratorPostProcessConsumer"/>, which
/// runs <see cref="RelocateTail"/> (write data: + dispatch) and lands exactly ONE
/// <see cref="EntryStepDispatch"/> on <c>queue:{processorId:D}</c> captured by
/// <see cref="CapturingDispatchConsumer"/>. Asserts, from the USER's perspective:
/// <list type="bullet">
///   <item>the dispatched <see cref="EntryStepDispatch"/> copies the handoff's StepId/ProcessorId/Payload,
///   threads its executionId UNCHANGED, and carries a non-empty EntryId (the data: relocation key = the
///   handoff envelope's MessageId);</item>
///   <item>the continuation is consumed exactly once (competing-consumer, not broadcast).</item>
/// </list>
/// The Pre fan-out + two-reason trip-end is proven directly in <c>OrchestratorPrePipelineFacts</c> /
/// <c>TypedResultConsumerFacts</c>; this fact closes the Post→processor hop on a live bus.
/// </summary>
public sealed class ResultConsumeTests
{
    /// <summary>An <see cref="IConnectionMultiplexer"/> succeeding the data: write + out: delete.</summary>
    private static IConnectionMultiplexer RedisOk()
    {
        var db = Substitute.For<IDatabase>();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(),
            Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>()).Returns(true);
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
            Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>()).Returns(true);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// Builds the in-memory harness binding the <see cref="OrchestratorPostProcessConsumer"/> on
    /// <c>orchestrator-result-post</c> (the Pre fan-out target) AND a <see cref="CapturingDispatchConsumer"/>
    /// short-name <c>{processorId:D}</c> per next-step processor (so the relocate-tail dispatch is captured).
    /// </summary>
    private static ServiceProvider BuildHarness(IEnumerable<Guid> processorIds, IConnectionMultiplexer redis)
    {
        var ids = processorIds.Distinct().ToArray();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(redis)
            .AddSingleton(Options.Create(new RetryOptions { Limit = 3 }))
            .AddSingleton(Options.Create(new OrchestratorOutputOptions { OutputDataTtlSeconds = 300 }))
            .AddSingleton(OrchestratorTestStubs.Metrics())
            .AddScoped<IStepDispatcher, StepDispatcher>()
            .AddScoped<RelocateTail>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<CapturingDispatchConsumer>();
                x.AddConsumer<OrchestratorPostProcessConsumer, OrchestratorPostProcessConsumerDefinition>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    foreach (var processorId in ids)
                        cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<CapturingDispatchConsumer>(ctx));
                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);
    }

    private static NextStepHandoff Handoff(Guid workflowId, Guid stepId, Guid processorId, string payload,
        string data, Guid executionId) =>
        new(workflowId, stepId, processorId, payload)
        { CorrelationId = Guid.NewGuid(), ExecutionId = executionId, Data = data };

    // ----- end-to-end Post hop: one field-copied dispatch for the handoff --------------------------

    [Fact]
    public async Task PostHandoff_DispatchesMatchingNextStep_WithCorrectFieldCopy()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        const string nextPayload = "{\"next\":true}";
        var executionId = Guid.NewGuid();

        var redis = RedisOk();
        await using var provider = BuildHarness([nextProcessorId], redis);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var post = await harness.Bus.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.ResultPost}"));
            await post.Send(Handoff(workflowId, nextStepId, nextProcessorId, nextPayload, "the-blob", executionId), ct);

            Assert.True(await harness.Consumed.Any<EntryStepDispatch>(ct));
            var dispatched = harness.Consumed.Select<EntryStepDispatch>(ct).Select(c => c.Context.Message).ToList();

            var msg = Assert.Single(dispatched);
            Assert.Equal(workflowId, msg.WorkflowId);
            Assert.Equal(nextStepId, msg.StepId);                // taken from the handoff (the next-step ids)
            Assert.Equal(nextProcessorId, msg.ProcessorId);
            Assert.Equal(nextPayload, msg.Payload);
            Assert.Equal(executionId, msg.ExecutionId);          // threaded UNCHANGED (REQ-71-11)
            Assert.NotEqual(Guid.Empty, msg.EntryId);            // the data: relocation key (= the post envelope id)
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    // ----- the continuation is consumed exactly once (competing-consumer, not broadcast) -----------

    [Fact]
    public async Task Continuation_ConsumedExactlyOnce_NotBroadcast()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();

        var redis = RedisOk();
        await using var provider = BuildHarness([nextProcessorId], redis);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var post = await harness.Bus.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.ResultPost}"));
            await post.Send(Handoff(workflowId, nextStepId, nextProcessorId, "{}", "the-blob", Guid.NewGuid()), ct);

            Assert.True(await harness.Consumed.Any<EntryStepDispatch>(ct));
            var dispatched = harness.Consumed.Select<EntryStepDispatch>(ct).ToList();
            Assert.Single(dispatched);   // one handoff -> one continuation, consumed once
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
