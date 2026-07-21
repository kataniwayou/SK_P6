using System.Text.Json;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using NSubstitute;
using Processor.Sample;
using StackExchange.Redis;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 70 (req 10) — <see cref="SampleProcessor"/>, the two-mode worked example, driven through the
/// framework seam (<c>SetSeamState</c> wires the per-dispatch state, then <c>ExecuteAsync</c> deserializes the
/// payload into a <see cref="SampleConfig"/> and invokes the typed transform — exactly as the pipeline does):
/// <list type="bullet">
///   <item><b>ENTRY</b> (<c>executionId == Guid.Empty</c>, Mode-2): spawns EXACTLY TWO completed
///   <see cref="DataResult"/>s to the <c>-post</c> queue with DISTINCT executionIds and the seam returns NULL.
///   PB-03: the author issues NO entry delete at the seam level — deletion is framework-owned (the pipeline
///   null-return tail, covered in PrePipelineFacts). Seeds the TWO FIXED deterministic values <c>100</c> and
///   <c>200</c> (Phase 73, D-01).</item>
///   <item><b>DOWNSTREAM</b> (<c>executionId != Guid.Empty</c>, Mode-1): returns ONE completed
///   <see cref="DataResult"/> reusing the inbound executionId (the inline tail runs it), no spawn, no delete,
///   producing <c>7 + 3 = 10</c>.</item>
/// </list>
/// Per D4/LOG-04 the concrete processor emits NO author logs — behaviour is proven ENTIRELY via
/// <c>send.SentData</c> / <c>dr.Data</c> (never via log assertions), and the framework verdict never depends
/// on a concrete-processor log. The D5/LOG-05 operator-freedom guard (the bus-wide execution-scope filter
/// stays registered unconditionally, so any author string still gets Tier-1 <c>attributes.*</c>) lives in
/// <c>ConsoleExecutionScopeFilterTests</c> to reuse its MassTransit harness without duplication.
/// </summary>
public sealed class SampleProcessorFacts
{
    private static void WireSeam(
        BaseProcessorBase processor, IDatabase db, DispatchTestKit.CapturingSendProvider send,
        Guid processorId)
        => processor.SetSeamState(
            db, send, retryLimit: 3,
            processorId: processorId,
            messageId: Guid.NewGuid(),
            workflowId: Guid.NewGuid(),
            stepId: Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            onSpawnDropped: _ => { });

    [Fact]
    public async Task Entry_Spawns_Two_Distinct_ExecIds_NoAuthorDelete_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var processor = new SampleProcessor();

        var processorId = Guid.NewGuid();
        var db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var send = new DispatchTestKit.CapturingSendProvider();
        WireSeam(processor, db, send, processorId);

        // ENTRY: executionId == Guid.Empty → Mode-2 fan-out.
        var payload = JsonSerializer.Serialize(new { number = 10, label = "Step_A1" }, ProcessorConfig.SerializerOptions);
        var dr = await ((BaseProcessorBase)processor).ExecuteAsync(
            System.Text.Encoding.UTF8.GetBytes("any-input"), payload, Guid.Empty, ct);

        Assert.Null(dr);                                            // seam returns null — Pre does nothing inline
        Assert.Equal(2, send.SentData.Count);                      // exactly two spawns to -post
        Assert.NotEqual(send.SentData[0].ExecutionId, send.SentData[1].ExecutionId);   // DISTINCT execIds
        Assert.All(send.SentDataToUri, t => Assert.EndsWith($"{processorId:D}-post", t.Uri.ToString()));
        Assert.All(send.SentData, x => Assert.Equal(StepOutcome.Completed, x.Result));
        Assert.Empty(send.Sent);                                   // no inline Step* on the entry path
        // Mode-2 seeds the TWO FIXED deterministic values (Phase 73, D-01) — NO random, config.number ignored.
        var seededNumbers = send.SentData
            .Select(x => JsonDocument.Parse(x.Data).RootElement.GetProperty("number").GetInt32())
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(new[] { 100, 200 }, seededNumbers);
        // PB-03: at the seam level (author-only, no pipeline) the author issues NO entry delete — deletion is
        // framework-owned on the pipeline null-return path (proven in PrePipelineFacts), not here.
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Downstream_Returns_One_Completed_ReusesInboundExec_NoSpawn_NoDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var processor = new SampleProcessor();

        var db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var send = new DispatchTestKit.CapturingSendProvider();
        WireSeam(processor, db, send, Guid.NewGuid());

        var inboundExec = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { number = 3, label = "Step_B" }, ProcessorConfig.SerializerOptions);
        var dr = await ((BaseProcessorBase)processor).ExecuteAsync(
            System.Text.Encoding.UTF8.GetBytes("{\"number\":7,\"label\":\"Step_B\"}"), payload, inboundExec, ct);

        Assert.NotNull(dr);
        Assert.Equal(StepOutcome.Completed, dr!.Result);
        Assert.Equal(inboundExec, dr.ExecutionId);                 // REUSE the inbound exec
        using var doc = JsonDocument.Parse(dr.Data);
        Assert.Equal(10, doc.RootElement.GetProperty("number").GetInt32());   // 7 + 3, NO random

        Assert.Empty(send.SentData);                               // no spawn (Mode-1)
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // Mode-1 seam issues no delete
    }

    [Fact]
    public async Task Entry_NullConfig_StillSpawnsTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        var processor = new SampleProcessor();

        var db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var send = new DispatchTestKit.CapturingSendProvider();
        WireSeam(processor, db, send, Guid.NewGuid());

        // empty payload → null config (baseNumber 0); still ENTRY (Guid.Empty) → two spawns.
        var dr = await ((BaseProcessorBase)processor).ExecuteAsync(
            System.Text.Encoding.UTF8.GetBytes("any-input"), "", Guid.Empty, ct);

        Assert.Null(dr);
        Assert.Equal(2, send.SentData.Count);
        // Mode-2 seeds the TWO FIXED deterministic values 100/200 (Phase 73, D-01) — independent of config,
        // so a null config still yields exactly {100, 200} (no random, no baseNumber offset).
        var seededNumbers = send.SentData
            .Select(spawn => JsonDocument.Parse(spawn.Data).RootElement.GetProperty("number").GetInt32())
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(new[] { 100, 200 }, seededNumbers);
    }

    [Fact]
    public void Deserializes_Typed_Config_From_Payload_Case_Insensitively()
    {
        var config = JsonSerializer.Deserialize<SampleConfig>(
            "{\"number\":5,\"label\":\"Step_A1\"}", ProcessorConfig.SerializerOptions);

        Assert.NotNull(config);
        Assert.Equal(5, config!.Number);
        Assert.Equal("Step_A1", config.Label);
    }
}
