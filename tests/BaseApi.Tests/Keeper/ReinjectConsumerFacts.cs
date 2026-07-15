using System.Diagnostics.Metrics;
using global::Keeper.Observability;
using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 75 (D75-4): a minimal capturing <see cref="ILogger{TCategoryName}"/> that materializes each
/// log entry's structured message-template state (the <c>{Placeholder}</c> args, exposed by MEL as an
/// <see cref="IReadOnlyList{T}"/> of <see cref="KeyValuePair{TKey,TValue}"/>) into a captured list, so a
/// hermetic fact can assert the keeper drop/reinject logs carry the (CorrelationId, ExecutionId, EntryId,
/// MessageId, ReinjectOutcome) join keys that will surface as ES <c>attributes.*</c> live (Pitfall 2). The
/// keeper's <c>RecoveryConsumerBase.Consume</c> opens NO BeginScope, so only explicit placeholder args are
/// captured here — mirroring how they will (not) arrive via ambient scope in production.
/// </summary>
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

/// <summary>
/// Phase 70 / req 6 (D-12): the Keeper REINJECT state reads L2[entryId]; present → re-injects a
/// reconstructed EntryStepDispatch carrying the Payload to queue:{ProcessorId} with the outbound envelope
/// MessageId overridden to the carried m.MessageId AND emits keeper_messages_sent (Phase 74); absent/empty
/// (STRLEN==0, no Redis exception) → BY-DESIGN silent drop (no throw, no send, no keeper_messages_sent).
/// <para>
/// Phase 74 / D-14: the legacy <c>keeper_reinject_dropped</c> counter is REMOVED — the by-design-drop test
/// asserts that NO <c>keeper_reinject_dropped</c> series (and no <c>keeper_messages_sent</c>) is emitted on
/// the drop path (one of the three removed-counter absence asserts), while the success path increments the
/// uniform <c>keeper_messages_sent</c>.
/// </para>
/// </summary>
public sealed class ReinjectConsumerFacts
{
    private static ConsumeContext<KeeperReinject> Ctx(KeeperReinject m, CancellationToken ct)
    {
        var ctx = Substitute.For<ConsumeContext<KeeperReinject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    [Fact]
    [Trait("Phase", "70")]
    public async Task Reinject_present_sends_EntryStepDispatch_with_envelope_messageId_override()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),   // req 6/A3: re-inject with the SAME messageId on the envelope
            Payload = "{\"cfg\":7}",
        };
        var db = RecoveryTestKit.Db();
        // IN-04: REINJECT gates on STRLEN, not a full StringGet. STRLEN > 0 → data present.
        db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>())
            .Returns(10L);   // present
        var send = new RecoveryTestKit.CapturingSendProvider();
        var metrics = RecoveryTestKit.Metrics();

        // Phase 74 (REQ-3/D-14): the success path MUST increment the uniform keeper_messages_sent counter
        // after the confirmed Send. Scope the listener to THIS metrics instance's exact instrument (by
        // reference identity) so the capture is hermetic under parallel test classes (a name-only filter
        // would also catch a concurrently-running test's keeper_messages_sent).
        long sent = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, metrics.MessagesSent))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.MessagesSent)) Interlocked.Add(ref sent, measurement);
        });
        listener.Start();

        var log = new CapturingLogger<ReinjectConsumer>();
        var consumer = new ReinjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(),
            metrics, log);

        await consumer.Consume(Ctx(m, ct));

        var (uri, msg) = Assert.Single(send.Sent);
        Assert.Equal(new Uri($"queue:{m.ProcessorId:D}"), uri);
        var dispatch = Assert.IsType<EntryStepDispatch>(msg);
        Assert.Equal(m.Payload, dispatch.Payload);
        Assert.Equal(m.EntryId, dispatch.EntryId);
        Assert.Equal(m.CorrelationId, dispatch.CorrelationId);
        Assert.Equal(m.ExecutionId, dispatch.ExecutionId);
        Assert.Equal(m.WorkflowId, dispatch.WorkflowId);
        Assert.Equal(m.StepId, dispatch.StepId);
        Assert.Equal(m.ProcessorId, dispatch.ProcessorId);

        // req 6: the outbound envelope MessageId is overridden to the carried m.MessageId.
        Assert.Equal(m.MessageId, Assert.Single(send.SentMessageIds));

        // Phase 74: the confirmed Send incremented the uniform keeper_messages_sent exactly once.
        Assert.Equal(1, Interlocked.Read(ref sent));

        // Phase 75 (D75-4): a structured LogInformation record carries all four join keys + the
        // "reinject" outcome discriminator as message-template placeholders (so they surface as ES
        // attributes.*), AND it sits AFTER CountSent (sent==1 above still holds).
        var info = Assert.Single(log.Entries, e => e.Level == LogLevel.Information);
        AssertJoinFields(info, m, outcome: "reinject");
    }

    [Fact]
    [Trait("Phase", "70")]
    public async Task Reinject_absent_drops_no_throw_no_send_and_emits_no_legacy_drop_counter()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            Payload = "{\"cfg\":7}",
        };
        // IN-04: STRLEN==0 covers BOTH a missing key AND an empty value (the absent-OR-empty drop case).
        // NSubstitute returns default(long) == 0 unstubbed, so the un-stubbed key reads as length 0 → drop.
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        // Phase 74 (REQ-3): the legacy keeper_reinject_dropped counter is REMOVED. Observe ALL "Keeper"-meter
        // measurements; the by-design drop must emit NO drop counter and NO keeper_messages_sent (a drop never
        // sends, so CountSent must not fire on the early-return path — D-02 / T-74-06). The full success-path
        // keeper_messages_sent / keeper_messages_consumed metric facts are owned by Plan 04 (D-14).
        var metrics = RecoveryTestKit.Metrics();

        long sent = 0;
        // D-14 absence assert: NO instrument named keeper_reinject_dropped may EVER be published on THIS
        // metrics instance's meter (the removed counter emits no series). Scope to this instance's meter (by
        // reference) so a concurrent test's "Keeper" meter cannot pollute the capture.
        var thisMeter = metrics.MessagesSent.Meter;
        var publishedKeeperInstruments = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, thisMeter))
            {
                publishedKeeperInstruments.Add(instrument.Name);
                if (ReferenceEquals(instrument, metrics.MessagesSent))
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.MessagesSent)) Interlocked.Add(ref sent, measurement);
        });
        listener.Start();

        var log = new CapturingLogger<ReinjectConsumer>();
        var consumer = new ReinjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(),
            metrics, log);

        await consumer.Consume(Ctx(m, ct));   // D-06: no throw

        Assert.Empty(send.Sent);                     // nothing re-injected when the data is gone
        Assert.Equal(0, Interlocked.Read(ref sent)); // T-74-06: keeper_messages_sent never fires on the drop path
        // D-14: the removed keeper_reinject_dropped counter publishes NO series.
        Assert.DoesNotContain("keeper_reinject_dropped", publishedKeeperInstruments);

        // Phase 75 (D75-4): the by-design DROP warning now carries all four join keys + the "drop"
        // outcome discriminator as message-template placeholders, so the analyzer can join a keeper
        // clean-absent drop to a lost (correlationId, executionId) via ES attributes.*.
        var warn = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        AssertJoinFields(warn, m, outcome: "drop");
    }

    /// <summary>Phase 75 (D75-4): assert a captured log entry's structured state carries the four join
    /// keys with the message's values plus the ReinjectOutcome discriminator (placeholder-form only).</summary>
    private static void AssertJoinFields(CapturingLogger<ReinjectConsumer>.Entry entry, KeeperReinject m, string outcome)
    {
        Assert.Contains(new KeyValuePair<string, object?>("CorrelationId", m.CorrelationId), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("ExecutionId", m.ExecutionId), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("EntryId", m.EntryId), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("MessageId", m.MessageId), entry.State);
        Assert.Contains(new KeyValuePair<string, object?>("ReinjectOutcome", outcome), entry.State);
    }
}
