using System.Text.Json;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
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
///   <see cref="DataResult"/>s to the <c>-post</c> queue with DISTINCT executionIds, DELETES the inbound entry,
///   and the seam returns NULL (the Pre consumer writes/sends/deletes nothing inline). Logs the
///   <c>"{label} had the following numbers: …"</c> line.</item>
///   <item><b>DOWNSTREAM</b> (<c>executionId != Guid.Empty</c>, Mode-1): returns ONE completed
///   <see cref="DataResult"/> reusing the inbound executionId (the inline tail runs it), no spawn, no delete.
///   Logs the same line.</item>
/// </list>
/// </summary>
public sealed class SampleProcessorFacts
{
    /// <summary>Records every log entry's level + formatted message so the "had the following numbers" line
    /// is observable hermetically.</summary>
    private sealed class CapturingLogger : ILogger<SampleProcessor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static void WireSeam(
        BaseProcessorBase processor, IDatabase db, DispatchTestKit.CapturingSendProvider send,
        Guid entryId, Guid processorId)
        => processor.SetSeamState(
            db, send, retryLimit: 3,
            entryId: entryId,
            processorId: processorId,
            messageId: Guid.NewGuid(),
            workflowId: Guid.NewGuid(),
            stepId: Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            onSpawnDropped: _ => { },
            escalateDelete: () => Task.CompletedTask);

    [Fact]
    public async Task Entry_Spawns_Two_Distinct_ExecIds_DeletesEntry_ReturnsNull_AndLogs()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        var entryId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var send = new DispatchTestKit.CapturingSendProvider();
        WireSeam(processor, db, send, entryId, processorId);

        // ENTRY: executionId == Guid.Empty → Mode-2 fan-out.
        var payload = JsonSerializer.Serialize(new { number = 10, label = "Step_A1" }, ProcessorConfig.SerializerOptions);
        var dr = await ((BaseProcessorBase)processor).ExecuteAsync("any-input", payload, Guid.Empty, ct);

        Assert.Null(dr);                                            // seam returns null — Pre does nothing inline
        Assert.Equal(2, send.SentData.Count);                      // exactly two spawns to -post
        Assert.NotEqual(send.SentData[0].ExecutionId, send.SentData[1].ExecutionId);   // DISTINCT execIds
        Assert.All(send.SentDataToUri, t => Assert.EndsWith($"{processorId:D}-post", t.Uri.ToString()));
        Assert.All(send.SentData, x => Assert.Equal(StepOutcome.Completed, x.Result));
        Assert.Empty(send.Sent);                                   // no inline Step* on the entry path
        // the inbound entry was deleted (Mode-2 DeleteEntry).
        await db.Received(1).KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        // logs the "{label} had the following numbers: …" line.
        Assert.Contains(logger.Entries, e => e.Message.Contains("had the following numbers"));
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task Downstream_Returns_One_Completed_ReusesInboundExec_NoSpawn_NoDelete_AndLogs()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        var entryId = Guid.NewGuid();
        var db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var send = new DispatchTestKit.CapturingSendProvider();
        WireSeam(processor, db, send, entryId, Guid.NewGuid());

        var inboundExec = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { number = 3, label = "Step_B" }, ProcessorConfig.SerializerOptions);
        var dr = await ((BaseProcessorBase)processor).ExecuteAsync(
            "{\"number\":7,\"label\":\"Step_B\"}", payload, inboundExec, ct);

        Assert.NotNull(dr);
        Assert.Equal(StepOutcome.Completed, dr!.Result);
        Assert.Equal(inboundExec, dr.ExecutionId);                 // REUSE the inbound exec
        using var doc = JsonDocument.Parse(dr.Data);
        Assert.Equal(10, doc.RootElement.GetProperty("number").GetInt32());   // 7 + 3, NO random

        Assert.Empty(send.SentData);                               // no spawn (Mode-1)
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // no DeleteEntry inline
        Assert.Contains(logger.Entries, e => e.Message.Contains("had the following numbers"));
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task Entry_NullConfig_StillSpawnsTwo_AndLogs()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        var db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var send = new DispatchTestKit.CapturingSendProvider();
        WireSeam(processor, db, send, Guid.NewGuid(), Guid.NewGuid());

        // empty payload → null config (baseNumber 0); still ENTRY (Guid.Empty) → two spawns.
        var dr = await ((BaseProcessorBase)processor).ExecuteAsync("any-input", "", Guid.Empty, ct);

        Assert.Null(dr);
        Assert.Equal(2, send.SentData.Count);
        Assert.Single(logger.Entries);
        foreach (var spawn in send.SentData)
        {
            using var doc = JsonDocument.Parse(spawn.Data);
            Assert.InRange(doc.RootElement.GetProperty("number").GetInt32(), 0, 99);   // baseNumber 0 + 0..99
        }
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
