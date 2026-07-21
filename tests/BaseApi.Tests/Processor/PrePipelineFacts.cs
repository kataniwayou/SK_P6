using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Processor.Sample;
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
///   <item>req 3 — seam returns null (Mode-2 handled spawn) → NO write, NO send; PB-03: the FRAMEWORK deletes
///   L2[entryId] on the null path (skipped for a Guid.Empty source; delete-exhaust → DELETE keeper).</item>
///   <item>req 4 — completed → write OutputData(messageId) once + StepCompleted + delete L2[entryId];
///   delete-exhaust → DELETE; write-exhaust → INJECT (no StepCompleted, no entry delete).</item>
/// </list>
/// </summary>
public sealed class PrePipelineFacts
{
    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send)
        => BuildWith(redis, context, processor, send);

    /// <summary>D-05 anchor (RESEARCH plain-construct): build the pipeline through its public ctor with ANY
    /// <see cref="ISendEndpointProvider"/> — e.g. the faulting <c>DispatchTestKit.SendFaultProvider</c> — so the
    /// PB-02/D-05 nack fact can drive a DEFEATED Mode-2 spawn (send-exhaust) through the real pipeline.</summary>
    private static ProcessorPipeline BuildWith(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        ISendEndpointProvider send)
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
    public async Task InputInvalid_OneStepFailed_WritesOutputData_AndNoEntryDelete()   // C-2 / req 2 / Phase 72 REQ-3
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        // present input "{}" fails a definition requiring "x" → business StepFailed BEFORE the seam.
        var context = Ctx(input: "{\"type\":\"object\",\"required\":[\"x\"]}");
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid());

        await Build(redis, context, processor, send).RunAsync(d, messageId, ct);

        var failed = Assert.IsType<StepFailed>(Assert.Single(send.Sent));   // exactly one StepFailed
        Assert.Equal(messageId, failed.EntryId);                           // REQ-2: real EntryId now routed through OutputTail
        Assert.False(processor.Invoked);                                   // failed before the seam ran
        // Phase 72 / REQ-3: the input-fail path now writes the out: blob carrying validatedData ("{}").
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);
        Assert.Equal((RedisValue)"{}", set.Value);
        // C-2/D-08: NO entry delete on the input-invalid path — assert generically AND on the exact key.
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(d.EntryId), Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper);
    }

    // ---- req 3 ----

    [Fact]
    public async Task SeamReturnsNull_Source_NoWrite_NoSend_NoDelete()   // PB-03/D-04: Guid.Empty source → IsSource skip
    {
        var ct = TestContext.Current.CancellationToken;
        // A Guid.Empty SOURCE dispatch skips the gate/read (SourceStep.IsSource) and drives Mode-2. The
        // framework null-path delete is guarded by !SourceStep.IsSource, so a source seed deletes NOTHING —
        // net-effect IDENTICAL to the old no-op author DeleteEntry on a Guid.Empty seed (Pitfall 4).
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);   // Mode-2 handled everything
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.True(processor.Invoked);             // the seam ran (and returned null)
        Assert.Empty(send.Sent);                    // NO Step* send
        Assert.Empty(send.SentKeeper);              // NO DELETE keeper (source skip, not an exhaust)
        Assert.Empty(DispatchTestKit.ReceivedStringSets(db));                                    // NO output write
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // NO entry delete (IsSource skip)
    }

    [Fact]
    public async Task SeamReturnsNull_NonSource_FrameworkDeletesEntry()   // PB-03: the null-path delete is framework-owned
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);   // Mode-2 returns null
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid());

        await Build(redis, Ctx(), processor, send).RunAsync(d, Guid.NewGuid(), ct);

        Assert.True(processor.Invoked);             // the seam ran (and returned null)
        Assert.Empty(send.Sent);                    // still no inline Step* on the null path
        Assert.Empty(send.SentKeeper);              // delete succeeded → no DELETE escalation
        // PB-03: the FRAMEWORK deletes L2[entryId] on the Mode-2 null-return path (the author issues no delete).
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(d.EntryId), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SeamReturnsNull_NonSource_DeleteExhaust_EscalatesDelete()   // PB-03: the DELETE escalation moved to the null path
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadOkDeleteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);   // Mode-2 returns null
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.Empty(send.Sent);                                    // no inline Step* on the null path
        Assert.Single(send.SentKeeper.OfType<KeeperDelete>());      // delete-exhaust → exactly one DELETE (escalation now on the null path)
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
    public async Task SeamThrows_StatusException_OneStepFailed_WritesOutputData_NoEntryDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(new FailedException("x"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), messageId, ct);

        var failed = Assert.IsType<StepFailed>(Assert.Single(send.Sent));
        Assert.Equal("x", failed.ErrorMessage);                        // e.Message survives the OutputTail routing
        Assert.Equal(messageId, failed.EntryId);                       // REQ-2/REQ-3: real EntryId (= out: blob key)
        Assert.Empty(send.SentKeeper);
        // Phase 72 / REQ-3: a seam-thrown Failed now writes L2[out:messageId] carrying validatedData ("{}").
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);
        Assert.Equal((RedisValue)"{}", set.Value);
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // C-2 parity
    }

    [Fact]
    public async Task SeamThrows_Cancelled_WritesOutputData_StampsEntryId()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(new CancelledException("c"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), messageId, ct);

        var cancelled = Assert.IsType<StepCancelled>(Assert.Single(send.Sent));
        Assert.Equal("c", cancelled.CancellationMessage);              // e.Message survives the routing
        Assert.Equal(messageId, cancelled.EntryId);                    // REQ-2/REQ-3: real EntryId
        Assert.Empty(send.SentKeeper);
        // Phase 72 / REQ-3: a seam-thrown Cancelled now writes L2[out:messageId] carrying validatedData ("{}").
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);
        Assert.Equal((RedisValue)"{}", set.Value);
    }

    [Fact]
    public async Task SeamThrows_Processing_NoWrite_EmptyEntryId()   // D-04: Processing keeps Guid.Empty + no blob
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(new ProcessingException("p"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        var processing = Assert.IsType<StepProcessing>(Assert.Single(send.Sent));
        Assert.Equal(Guid.Empty, processing.EntryId);                 // D-04: Processing keeps Guid.Empty
        // D-04: the transient Processing status rides the OutputTail result!=Processing gate → NO out: blob.
        Assert.Empty(DispatchTestKit.ReceivedStringSets(db));
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task SeamThrows_Unexpected_Failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var messageId = Guid.NewGuid();
        var processor = new DispatchTestKit.FakeProcessor(new InvalidOperationException("boom"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), messageId, ct);

        var failed = Assert.IsType<StepFailed>(Assert.Single(send.Sent));
        // WR-03: the unexpected/deserialize branch emits a SANITIZED constant on the wire — never ex.Message
        // (which for a JsonException can carry payload bytes). The raw "boom" must NOT leak to the result.
        Assert.Equal("input deserialization failed", failed.ErrorMessage);
        Assert.DoesNotContain("boom", failed.ErrorMessage);
        Assert.Equal(messageId, failed.EntryId);                       // REQ-2/REQ-3: real EntryId now routed through OutputTail
        // Phase 72 / REQ-3: the unexpected catch now writes L2[out:messageId] carrying validatedData ("{}").
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);
        Assert.Equal((RedisValue)"{}", set.Value);
        Assert.Empty(send.SentKeeper);
    }

    /// <summary>A trivial author config for the real-subclass deser-failure fact.</summary>
    private sealed record DeserConfig(string? Value) : BaseProcessor.Core.Configuration.ProcessorConfig;

    /// <summary>A REAL <see cref="BaseProcessor{DeserConfig}"/> so the framework actually runs
    /// <c>JsonSerializer.Deserialize</c> — a malformed payload throws inside ExecuteAsync before the seam body.</summary>
    private sealed class RealDeserProcessor : BaseProcessor<DeserConfig>
    {
        protected override Task<DataResult?> ProcessAsync(
            byte[] validatedData, DeserConfig? config, Guid executionId, CancellationToken ct)
            => Task.FromResult<DataResult?>(null);   // never reached on a malformed payload
    }

    [Fact]
    public async Task MalformedPayload_DeserFailure_OneStepFailed_WritesOutputData_NoEntryDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        // "not json" → JsonException inside BaseProcessor<DeserConfig>.ExecuteAsync → pipeline catch-all → one StepFailed.
        await Build(redis, Ctx(), new RealDeserProcessor(), send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid(), "not json"), messageId, ct);

        var failed = Assert.IsType<StepFailed>(Assert.Single(send.Sent));
        Assert.Equal("input deserialization failed", failed.ErrorMessage);   // WR-03: sanitized constant on the wire
        Assert.Equal(messageId, failed.EntryId);                             // REQ-2/REQ-3: real EntryId
        // Phase 72 / REQ-3: the deser-catch now writes L2[out:messageId] carrying validatedData ("{}").
        var set = Assert.Single(DispatchTestKit.ReceivedStringSets(db));
        Assert.Equal((RedisKey)L2ProjectionKeys.OutputData(messageId), set.Key);
        Assert.Equal((RedisValue)"{}", set.Value);
        Assert.Empty(send.SentKeeper);
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // C-2/D-08 parity
    }

    // ---- PB-02 / D-05: a DEFEATED Mode-2 spawn PROPAGATES (nack), it does NOT ack as a StepFailed ----

    [Fact]
    public async Task SpawnExhaust_Propagates_SpawnSendExhaustedException_Nack_NoStepFailed()   // PB-02 / D-05
    {
        var ct = TestContext.Current.CancellationToken;
        // A Guid.Empty SOURCE dispatch skips the gate/read (SourceStep.IsSource) and drives the real
        // SampleProcessor entry fan-out (Open-Q 2). The redis is barely touched (no L2 input on a source).
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out _);
        var processor = new SampleProcessor();
        // Transient RedisConnectionException on the -post send ⇒ SpawnToPost exhausts its RetryLoop and throws
        // SpawnSendExhaustedException (Plan 01) — the fail-loud signal this plan converts into a nack.
        var faultingSend = new DispatchTestKit.SendFaultProvider();

        var pipeline = BuildWith(redis, Ctx(), processor, faultingSend);

        // The defeated spawn's dedicated exception RE-PROPAGATES out of RunAsync (the narrow catch's bare throw)
        // → MassTransit nack-requeue. The THROW is the terminal signal (PB-02): the pipeline never reached the
        // generic catch / OutputTail, so NO StepFailed was produced (it did NOT take the ack path).
        await Assert.ThrowsAsync<SpawnSendExhaustedException>(() => pipeline.RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct));
    }

    // ---- D-03 (poison-safety negative control): a DETERMINISTIC seam fault STAYS on the ack path ----

    [Fact]
    public async Task DeterministicSeamFault_DoesNotEscape_OneStepFailed_Ack()   // D-03 negative control
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(new InvalidOperationException("boom"));
        var send = new DispatchTestKit.CapturingSendProvider();

        // The narrow SpawnSendExhaustedException filter MUST NOT let a deterministic fault escape — an
        // InvalidOperationException stays in the generic catch → StepFailed + ack (no throw, no nack-loop).
        var ex = await Record.ExceptionAsync(() => Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), messageId, ct));
        Assert.Null(ex);   // D-03: RunAsync did NOT throw — the deterministic fault did not nack

        var failed = Assert.IsType<StepFailed>(Assert.Single(send.Sent));   // exactly one StepFailed (ack path)
        Assert.Equal(messageId, failed.EntryId);
        Assert.Empty(send.SentKeeper);
    }
}
