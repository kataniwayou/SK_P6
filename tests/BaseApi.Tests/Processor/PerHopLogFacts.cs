using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 76 (FW-01 / D-05/D-06/D-08/D-09/D-10, + D-18 consumer) — the framework-emitted per-hop execution
/// record from <see cref="ProcessorPipeline"/>. One structured <see cref="LogLevel.Information"/> record per
/// EXECUTED hop, for every terminal outcome (Completed/Failed/Cancelled), carrying the six ids + outcome
/// ONLY, on the 5 executed-hop branches — and NOTHING on the non-execution branches (clean-absent, reinject,
/// Processing). Facts:
/// <list type="bullet">
///   <item>A Completed hop yields exactly ONE Information record with all six keys (StepId, ExecutionId,
///   CorrelationId, EntryId, MessageId, Outcome="Completed").</item>
///   <item>A Failed hop (input-schema fail) → Outcome="Failed"; a Cancelled seam → Outcome="Cancelled".</item>
///   <item>A processor that writes NO author log still yields exactly one framework record.</item>
///   <item>The Mode-2 entry step (d.ExecutionId == Guid.Empty) yields one record whose ExecutionId is the
///   all-zeros marker (D-09, explicit).</item>
///   <item>D-18: a normal hop whose output blob fails the output schema logs Outcome="Failed".</item>
///   <item>Clean-absent / gate-fault reinject / Processing branches yield ZERO per-hop records.</item>
/// </list>
/// </summary>
public sealed class PerHopLogFacts
{
    /// <summary>Structured-state capturing logger (Pitfall 1: records only explicit placeholder args, not
    /// scope) — the exact pattern from <c>ReinjectConsumerFacts.CapturingLogger&lt;T&gt;</c>.</summary>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        internal sealed record Entry(LogLevel Level, IReadOnlyList<KeyValuePair<string, object?>> State);

        private readonly List<Entry> _entries = new();
        public IReadOnlyList<Entry> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var pairs = state is IReadOnlyList<KeyValuePair<string, object?>> kvps
                ? kvps.ToList()
                : new List<KeyValuePair<string, object?>>();
            _entries.Add(new Entry(logLevel, pairs));
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send, CapturingLogger<ProcessorPipeline> log)
    {
        var tail = new OutputTail(redis, context, send, DispatchTestKit.Retry(3),
            DispatchTestKit.Options(300), DispatchTestKit.Metrics());
        return new ProcessorPipeline(redis, context, processor, send, DispatchTestKit.Retry(3), tail,
            DispatchTestKit.Metrics(), log);
    }

    private static FakeProcessorContext Ctx(string? input = null, string? output = null) =>
        new() { InputDefinition = input, OutputDefinition = output };

    /// <summary>The per-hop framework records among all captured entries: an Information entry whose state
    /// carries the "Outcome" discriminator key (excludes the pre-existing ProcessAsync-processing-status log).</summary>
    private static IReadOnlyList<CapturingLogger<ProcessorPipeline>.Entry> HopRecords(
        CapturingLogger<ProcessorPipeline> log) =>
        log.Entries
            .Where(e => e.Level == LogLevel.Information && e.State.Any(kv => kv.Key == "Outcome"))
            .ToList();

    private static void AssertSixFields(
        CapturingLogger<ProcessorPipeline>.Entry entry, EntryStepDispatch d, Guid messageId, string outcome)
    {
        Assert.Contains(new KeyValuePair<string, object?>("StepId", d.StepId), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("ExecutionId", d.ExecutionId), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("CorrelationId", d.CorrelationId), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("EntryId", d.EntryId), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("MessageId", messageId), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("Outcome", outcome), entry.State);
    }

    // ---- the 5 executed-hop branches ----

    [Fact]
    public async Task Completed_LogsOneInformationRecord_WithSixFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()) with { ExecutionId = Guid.NewGuid() };

        await Build(redis, Ctx(), processor, send, log).RunAsync(d, messageId, ct);

        var rec = Assert.Single(HopRecords(log));
        AssertSixFields(rec, d, messageId, "Completed");
    }

    [Fact]
    public async Task InputSchemaFail_LogsFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        // present input "{}" fails a definition requiring "x" → StepFailed BEFORE the seam (:105 branch).
        var context = Ctx(input: "{\"type\":\"object\",\"required\":[\"x\"]}");
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()) with { ExecutionId = Guid.NewGuid() };

        await Build(redis, context, processor, send, log).RunAsync(d, messageId, ct);

        var rec = Assert.Single(HopRecords(log));
        AssertSixFields(rec, d, messageId, "Failed");
    }

    [Fact]
    public async Task SeamThrowsCancelled_LogsCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(new CancelledException("c"));   // :138 branch
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()) with { ExecutionId = Guid.NewGuid() };

        await Build(redis, Ctx(), processor, send, log).RunAsync(d, messageId, ct);

        var rec = Assert.Single(HopRecords(log));
        AssertSixFields(rec, d, messageId, "Cancelled");
    }

    [Fact]
    public async Task SeamThrowsUnexpected_LogsFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(new InvalidOperationException("boom"));   // :153 branch
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()) with { ExecutionId = Guid.NewGuid() };

        await Build(redis, Ctx(), processor, send, log).RunAsync(d, messageId, ct);

        var rec = Assert.Single(HopRecords(log));
        AssertSixFields(rec, d, messageId, "Failed");
    }

    [Fact]
    public async Task NoAuthorLog_StillLogsExactlyOneFrameworkRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        // The FakeProcessor writes NO log of its own — the framework record must appear regardless.
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()) with { ExecutionId = Guid.NewGuid() };

        await Build(redis, Ctx(), processor, send, log).RunAsync(d, messageId, ct);

        Assert.Single(HopRecords(log));                              // exactly one framework record
        Assert.Equal(1, log.Entries.Count(e => e.Level == LogLevel.Information));   // and it is the only Information log
    }

    [Fact]
    public async Task Mode2EntryMarker_LogsCompleted_WithEmptyExecutionId()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor((DataResult?)null);   // Mode-2 spawn → :157 branch
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();
        // Entry step: ExecutionId == Guid.Empty (the DispatchTestKit default — D-09 all-zeros marker).
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid());
        Assert.Equal(Guid.Empty, d.ExecutionId);                    // precondition: this IS the entry step

        await Build(redis, Ctx(), processor, send, log).RunAsync(d, messageId, ct);

        var rec = Assert.Single(HopRecords(log));
        AssertSixFields(rec, d, messageId, "Completed");
        // D-09: the all-zeros ExecutionId is carried EXPLICITLY (not omitted), so the marker surfaces in ES.
        Assert.Contains(new KeyValuePair<string, object?>("ExecutionId", Guid.Empty), rec.State);
    }

    [Fact]
    public async Task D18_OutputSchemaForcedFailed_LogsFailed_NotCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        // Seam returns Completed with data "{}" — but the OutputDefinition requires "x", so OutputTail
        // downgrades to Failed (:56-59). The per-hop record must log the RESOLVED outcome (D-18).
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "{}"));
        var context = Ctx(output: "{\"type\":\"object\",\"required\":[\"x\"]}");
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()) with { ExecutionId = Guid.NewGuid() };

        await Build(redis, context, processor, send, log).RunAsync(d, messageId, ct);

        var rec = Assert.Single(HopRecords(log));
        AssertSixFields(rec, d, messageId, "Failed");               // D-18: NOT "Completed"
    }

    // ---- non-execution branches: ZERO per-hop records ----

    [Fact]
    public async Task CleanAbsentEntry_LogsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.CleanAbsentL2(out _);           // :82 clean-absent
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();

        await Build(redis, Ctx(), processor, send, log).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.Empty(HopRecords(log));                             // clean-absent → no hop executed → no record
    }

    [Fact]
    public async Task GateFaultReinject_LogsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.GateFaultL2(out _);            // :81 reinject
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Result(StepOutcome.Completed, "out"));
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();

        await Build(redis, Ctx(), processor, send, log).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.Empty(HopRecords(log));                             // reinject → hop not executed → no record
    }

    [Fact]
    public async Task SeamThrowsProcessing_LogsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(new ProcessingException("p"));   // Processing → skip
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();

        await Build(redis, Ctx(), processor, send, log).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.Empty(HopRecords(log));                             // Processing is not a terminal executed hop
    }
}
