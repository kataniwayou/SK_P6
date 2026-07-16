using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 76 (FW-04 / T-76-02) — the per-hop record is emitted on a NON-BLOCKING guarded path:
/// <list type="bullet">
///   <item>A <see cref="ThrowingLogger{T}"/> whose <c>Log</c> throws does NOT propagate out of
///   <see cref="ProcessorPipeline.RunAsync"/>, and the pipeline reaches the SAME terminal outcome
///   (write + StepCompleted + entry delete) as with a normal logger.</item>
///   <item>The OTLP logs export is configured batch/drop (structural assertion on the observability wiring),
///   not Simple/blocking — a stalled exporter cannot apply backpressure into the consume path.</item>
/// </list>
/// </summary>
public sealed class NonBlockingLogFacts
{
    /// <summary>An <see cref="ILogger{T}"/> whose every <c>Log</c> call throws — the adversarial exporter/logger
    /// the FW-04 swallow-guard must absorb without failing or delaying the hop.</summary>
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("adversarial logger: Log always throws (FW-04)");

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send, ILogger<ProcessorPipeline> log)
    {
        var tail = new OutputTail(redis, context, send, DispatchTestKit.Retry(3),
            DispatchTestKit.Options(300), DispatchTestKit.Metrics());
        return new ProcessorPipeline(redis, context, processor, send, DispatchTestKit.Retry(3), tail,
            DispatchTestKit.Metrics(), log);
    }

    private static FakeProcessorContext Ctx() => new() { InputDefinition = null, OutputDefinition = null };

    [Fact]
    public async Task ThrowingLogger_NeitherFailsNorChangesOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "the-output"));

        // Baseline: a normal (null) logger — the known-good completed outcome (write + StepCompleted + delete).
        var redisOk = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var dbOk);
        var sendOk = new DispatchTestKit.CapturingSendProvider();
        var dOk = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid());
        await Build(redisOk, Ctx(), processor, sendOk, NullLogger<ProcessorPipeline>.Instance).RunAsync(dOk, messageId, ct);

        // Adversarial: a ThrowingLogger. RunAsync must NOT surface the exception and must reach the SAME outcome.
        var redisThrow = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var dbThrow);
        var sendThrow = new DispatchTestKit.CapturingSendProvider();
        var processor2 = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "the-output"));
        var dThrow = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid());

        // (1) no exception surfaces from RunAsync despite the logger throwing on every Log call.
        var ex = await Record.ExceptionAsync(() =>
            Build(redisThrow, Ctx(), processor2, sendThrow, new ThrowingLogger<ProcessorPipeline>()).RunAsync(dThrow, messageId, ct));
        Assert.Null(ex);

        // (2) the pipeline outcome is UNCHANGED vs the normal-logger run: one OutputData write, one
        // StepCompleted, one entry delete — the observability failure did not alter the hop.
        Assert.Single(DispatchTestKit.ReceivedStringSets(dbThrow));
        Assert.Single(sendThrow.Sent.OfType<StepCompleted>());
        Assert.Empty(sendThrow.SentKeeper);
        await dbThrow.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(dThrow.EntryId), Arg.Any<CommandFlags>());

        // parity with the baseline good run.
        Assert.Equal(sendOk.Sent.OfType<StepCompleted>().Count(), sendThrow.Sent.OfType<StepCompleted>().Count());
        Assert.Equal(DispatchTestKit.ReceivedStringSets(dbOk).Count, DispatchTestKit.ReceivedStringSets(dbThrow).Count);
    }

    [Fact]
    public void ExportShape_LogsOtlp_IsBatchNotSimple()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, "src", "BaseConsole.Core", "DependencyInjection",
            "BaseConsoleObservabilityExtensions.cs");
        Assert.True(File.Exists(path), $"expected observability wiring at {path}");
        var text = File.ReadAllText(path);

        // FW-04: the logs OTLP exporter declares the batch/drop processor EXPLICITLY (never blocking Simple).
        Assert.Contains("ExportProcessorType.Batch", text);
        Assert.DoesNotContain("ExportProcessorType.Simple", text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
