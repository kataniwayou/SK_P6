using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 70 (D-14 / req 1-4) — the linear PRE-PROCESS flow of <see cref="ProcessorPipeline"/>:
/// <list type="number">
///   <item>req 1 — gate on <c>exist L2[entryId]</c>: a fault → exactly one REINJECT (no Step*); a clean-absent
///   entry → return WITHOUT processing.</item>
///   <item>req 2 — read L2[entryId]: a fault → REINJECT; an input-schema failure → exactly one StepFailed AND
///   <b>NO entry delete</b> (C-2 — the entry is left to its TTL).</item>
///   <item>req 3 — seam returns null (Mode-2 handled spawn) → NO write, NO send, NO delete.</item>
///   <item>req 4 — completed → write OutputData(messageId) once + StepCompleted + delete L2[entryId];
///   delete-exhaust → DELETE; write-exhaust → INJECT (no StepCompleted, no entry delete).</item>
/// </list>
/// </summary>
public sealed class PrePipelineFacts
{
    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send)
    {
        var tail = new OutputTail(redis, context, send, DispatchTestKit.Retry(3),
            DispatchTestKit.Options(300), DispatchTestKit.Metrics());
        return new ProcessorPipeline(redis, context, processor, send, DispatchTestKit.Retry(3), tail,
            DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);
    }

    private static FakeProcessorContext Ctx(string? input = null, string? output = null) =>
        new() { InputDefinition = input, OutputDefinition = output };

    // ---- req 1 ----

    [Fact]
    public async Task GateFault_Reinject_NoStep_NoDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.GateFaultL2(out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.Single(send.SentKeeper.OfType<KeeperReinject>());     // exactly one REINJECT
        Assert.Empty(send.Sent);                                     // no Step* sent
        Assert.False(processor.Invoked);                             // never reached the seam
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task CleanAbsentEntry_NoProcessing_NoSend()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.CleanAbsentL2(out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.False(processor.Invoked);            // clean-absent → no processing
        Assert.Empty(send.Sent);
        Assert.Empty(send.SentKeeper);
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    // ---- req 2 ----

    [Fact]
    public async Task ReadFault_Reinject()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.ReadFaultL2(out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(input: "{}"), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.Single(send.SentKeeper.OfType<KeeperReinject>());     // exactly one REINJECT
        Assert.False(processor.Invoked);
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task InputInvalid_OneStepFailed_AndNoEntryDelete()   // C-2 / req 2
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        // present input "{}" fails a definition requiring "x" → business StepFailed BEFORE the seam.
        var context = Ctx(input: "{\"type\":\"object\",\"required\":[\"x\"]}");
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid());

        await Build(redis, context, processor, send).RunAsync(d, Guid.NewGuid(), ct);

        Assert.IsType<StepFailed>(Assert.Single(send.Sent));         // exactly one StepFailed
        Assert.False(processor.Invoked);                             // failed before the seam ran
        // C-2: NO entry delete on the input-invalid path — assert generically AND on the exact key.
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(d.EntryId), Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper);
    }

    // ---- req 3 ----

    [Fact]
    public async Task SeamReturnsNull_NoWrite_NoSend_NoDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);   // Mode-2 handled everything
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.True(processor.Invoked);             // the seam ran (and returned null)
        Assert.Empty(send.Sent);                    // NO Step* send
        Assert.Empty(send.SentKeeper);
        Assert.Empty(DispatchTestKit.ReceivedStringSets(db));                                    // NO output write
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // NO entry delete
    }

    // ---- req 4 ----

    [Fact]
    public async Task Completed_WritesOutputData_SendsStepCompleted_DeletesEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "the-output"));
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid());

        await Build(redis, Ctx(), processor, send).RunAsync(d, messageId, ct);

        // one OutputData write keyed by the CARRIED messageId (the pipeline stamps dr with { MessageId = messageId }).
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);
        Assert.Single(send.Sent.OfType<StepCompleted>());           // one StepCompleted
        // exactly one entry delete keyed by ExecutionData(entryId).
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(d.EntryId), Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task Completed_DeleteExhaust_EscalatesDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadOkDeleteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "the-output"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.Single(send.Sent.OfType<StepCompleted>());           // the output tail still completed
        Assert.Single(send.SentKeeper.OfType<KeeperDelete>());      // delete-exhaust → one DELETE
    }

    [Fact]
    public async Task Completed_WriteExhaust_Injects_NoStepCompleted_NoEntryDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.OutputWriteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "the-output"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.Single(send.SentKeeper.OfType<KeeperInject>());      // write-exhaust → one INJECT
        Assert.Empty(send.Sent.OfType<StepCompleted>());           // NO StepCompleted
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // NO entry delete
    }

    // ---- seam-throw parity (C-2): a ProcessStatusException throw → one Step*, NO entry delete ----

    [Fact]
    public async Task SeamThrows_StatusException_OneStepFailed_NoEntryDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(new FailedException("x"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        var failed = Assert.IsType<StepFailed>(Assert.Single(send.Sent));
        Assert.Equal("x", failed.ErrorMessage);
        Assert.Empty(send.SentKeeper);
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // C-2 parity
    }

    [Fact]
    public async Task SeamThrows_Cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(new CancelledException("c"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        var cancelled = Assert.IsType<StepCancelled>(Assert.Single(send.Sent));
        Assert.Equal("c", cancelled.CancellationMessage);
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task SeamThrows_Processing()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(new ProcessingException("p"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.IsType<StepProcessing>(Assert.Single(send.Sent));
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task SeamThrows_Unexpected_Failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(new InvalidOperationException("boom"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        var failed = Assert.IsType<StepFailed>(Assert.Single(send.Sent));
        Assert.Equal("boom", failed.ErrorMessage);
        Assert.Empty(send.SentKeeper);
    }

    /// <summary>A trivial author config for the real-subclass deser-failure fact.</summary>
    private sealed record DeserConfig(string? Value) : BaseProcessor.Core.Configuration.ProcessorConfig;

    /// <summary>A REAL <see cref="BaseProcessor{DeserConfig}"/> so the framework actually runs
    /// <c>JsonSerializer.Deserialize</c> — a malformed payload throws inside ExecuteAsync before the seam body.</summary>
    private sealed class RealDeserProcessor : BaseProcessor<DeserConfig>
    {
        protected override Task<DataResult?> ProcessAsync(
            string validatedData, DeserConfig? config, Guid executionId, CancellationToken ct)
            => Task.FromResult<DataResult?>(null);   // never reached on a malformed payload
    }

    [Fact]
    public async Task MalformedPayload_DeserFailure_OneStepFailed_NoEntryDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        // "not json" → JsonException inside BaseProcessor<DeserConfig>.ExecuteAsync → pipeline catch-all → one StepFailed.
        await Build(redis, Ctx(), new RealDeserProcessor(), send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid(), "not json"), Guid.NewGuid(), ct);

        Assert.IsType<StepFailed>(Assert.Single(send.Sent));
        Assert.Empty(send.SentKeeper);
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // C-2 parity
    }
}
