using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 77 (D3 / LOG-03) — the framework result-sent record from <see cref="OutputTail.RunAsync"/>. The
/// processor result send now MINTS its OUTBOUND envelope MessageId, STAMPS it on the Step* envelope via the
/// <c>ctx =&gt; ctx.MessageId = outboundId</c> override, and logs exactly one
/// <see cref="LogLevel.Information"/> record <c>"result sent {MessageId} {Outcome}"</c> — symmetric with the
/// consume side's per-hop <c>hop executed {MessageId}</c>. This fact proves the LOGGED <c>{MessageId}</c>
/// equals the id actually STAMPED on the outbound envelope (captured by
/// <see cref="DispatchTestKit.CapturingSendProvider.SentMessageIds"/>), that <c>{Outcome}</c> is the resolved
/// terminal outcome, and that NO payload (dr.Data) leaks into the record (FW-03).
/// </summary>
public sealed class ResultSendLogFacts
{
    /// <summary>Structured-state capturing logger (records only explicit placeholder args, not scope) — the
    /// NullScope pattern copied from <see cref="PerHopLogFacts"/>/<c>ReinjectConsumerFacts</c>.</summary>
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

    private static OutputTail Build(
        IConnectionMultiplexer redis, DispatchTestKit.CapturingSendProvider send,
        CapturingLogger<OutputTail> log) =>
        new(redis, new FakeProcessorContext(), send, DispatchTestKit.Retry(3),
            DispatchTestKit.Options(300), DispatchTestKit.Metrics(), log);

    /// <summary>D3/LOG-03: driving a Completed result through <see cref="OutputTail.RunAsync"/> yields exactly
    /// one Information record whose logged <c>{MessageId}</c> equals the single id stamped on the outbound
    /// envelope, carries <c>{Outcome} == "Completed"</c>, and leaks no payload.</summary>
    [Fact]
    public async Task ResultSent_LogsOutboundMessageId_MatchingStampedEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(new Dictionary<string, string>(), out _);
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<OutputTail>();

        var dr = DispatchTestKit.Result(StepOutcome.Completed, "{\"n\":1}", messageId);
        var (proceed, resolved) = await Build(redis, send, log).RunAsync(dr, deleteEntryId: Guid.NewGuid(), ct);

        Assert.True(proceed);
        Assert.Equal(StepOutcome.Completed, resolved);

        // exactly one framework result-sent record (Information + a MessageId key)
        var rec = Assert.Single(log.Entries, e =>
            e.Level == LogLevel.Information && e.State.Any(kv => kv.Key == "MessageId"));

        // the LOGGED MessageId equals the single id actually STAMPED on the outbound envelope
        var stamped = Assert.Single(send.SentMessageIds);
        Assert.Contains(new KeyValuePair<string, object?>("MessageId", stamped), rec.State);
        Assert.Contains(new KeyValuePair<string, object?>("Outcome", "Completed"), rec.State);

        // FW-03: no payload/data ever appears in the record
        Assert.DoesNotContain(rec.State, kv => kv.Key is "Payload" or "Data");
    }
}
