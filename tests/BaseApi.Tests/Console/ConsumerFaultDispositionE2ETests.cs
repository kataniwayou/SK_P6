using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// The four possible dispositions of a message whose consumer throws, on the console bus
/// configuration under measurement.
/// <para>
/// <see cref="Unpinned"/> is the DELIBERATE initial state of
/// <c>ConsumerFaultDispositionE2ETests.PinnedDisposition</c>: the test CANNOT pass until a human has
/// run it against the real broker, read the evidence block, and pinned the observed value. That is
/// what makes this an honest proof rather than a presupposition dressed up as a test — the
/// discriminator that maps observations onto these members was written BEFORE the first run, and
/// every member below was reachable at that moment.
/// </para>
/// </summary>
public enum FaultDisposition
{
    /// <summary>Nothing has been observed yet. Never a valid pinned value.</summary>
    Unpinned,

    /// <summary>
    /// RabbitMQ redelivers the message to the SAME queue: the consumer is invoked repeatedly and no
    /// <c>&lt;queue&gt;_error</c> queue ever appears. This is what the <c>src/</c> doc-comments assert.
    /// </summary>
    NackRequeueRedelivery,

    /// <summary>
    /// MassTransit's DEFAULT error transport moves the message to <c>&lt;queue&gt;_error</c> after a
    /// single consume attempt and ACKs the original delivery. The <c>src/</c> doc-comments would be wrong.
    /// </summary>
    ErrorTransportPark,

    /// <summary>
    /// A bounded number (&gt;1) of consume attempts, then the message is parked in
    /// <c>&lt;queue&gt;_error</c>. Some retry policy is in play in addition to the error transport.
    /// </summary>
    BoundedRetryThenPark,

    /// <summary>
    /// The observations matched none of the above shapes. NEVER pinned — an inconclusive measurement
    /// is a finding to report, not a result to record.
    /// </summary>
    Inconclusive,
}

/// <summary>
/// The probe message. Its MassTransit message-type exchange is named by namespace:TypeName, i.e.
/// <c>BaseApi.Tests.Console:ProbeFaultMessage</c>; a faulted consume additionally publishes into
/// <c>MassTransit.Fault[[BaseApi.Tests.Console:ProbeFaultMessage, BaseApi.Tests, ...]]</c>. The
/// cleanup in the test targets BOTH by the substring <c>ProbeFaultMessage</c>.
/// <para>
/// Deliberately a plain record with no contract interface: the console bus's
/// <c>InboundExecutionScopeConsumeFilter&lt;T&gt;</c> short-circuits to <c>next.Send(context)</c>
/// unless the message implements <c>IExecutionCorrelated</c>, so the correlation filters are pure
/// pass-through here and cannot influence the measured disposition.
/// </para>
/// </summary>
public sealed record ProbeFaultMessage(Guid RunId);

/// <summary>
/// STATIC invocation recorder. MassTransit builds a FRESH scoped consumer instance per delivery, so
/// an instance-level counter would always read 1 and would silently fake the "invoked exactly once"
/// answer — the single most dangerous measurement artifact available to this experiment (T-kpl-06).
/// </summary>
internal static class ProbeFaultRecorder
{
    private static readonly ConcurrentQueue<TimeSpan> _offsets = new();
    private static readonly Stopwatch _clock = new();
    private static int _invocations;

    /// <summary>Total consume attempts observed since the last <see cref="Reset"/>.</summary>
    public static int Invocations => Volatile.Read(ref _invocations);

    /// <summary>Offsets of each consume attempt, measured from <see cref="Reset"/>.</summary>
    public static IReadOnlyList<TimeSpan> Offsets => _offsets.ToArray();

    public static void Reset()
    {
        Interlocked.Exchange(ref _invocations, 0);
        _offsets.Clear();
        _clock.Restart();
    }

    public static void Record()
    {
        Interlocked.Increment(ref _invocations);
        _offsets.Enqueue(_clock.Elapsed);
    }
}

/// <summary>
/// The always-throwing consumer. Deterministic: EVERY delivery faults identically (no
/// counter-conditional throw, no first-time-only) because the question under measurement is what the
/// transport does with a fault, not what it does with an eventual success.
/// </summary>
public sealed class ProbeFaultConsumer : IConsumer<ProbeFaultMessage>
{
    public async Task Consume(ConsumeContext<ProbeFaultMessage> context)
    {
        ProbeFaultRecorder.Record();

        // LOAD-BEARING: CancellationToken.None, NEVER context.CancellationToken. A cancellation-shaped
        // exception raised on bus stop is NOT the fault under measurement and MassTransit treats it
        // differently — threading the consume token in here would corrupt the result.
        // The 250 ms is observation hygiene: if the answer turns out to be nack-requeue, it bounds an
        // otherwise unbounded hot redelivery loop against the LIVE broker to ~4 deliveries/second (T-kpl-02).
#pragma warning disable xUnit1051 // deliberate: the consume token must not participate in this fault
        await Task.Delay(250, CancellationToken.None);
#pragma warning restore xUnit1051

        throw new InvalidOperationException(
            $"PROBE-FAULT {context.Message.RunId:N} — deliberate, measurement only");
    }
}

/// <summary>
/// BROKER-LITERAL PROOF (quick-260729-kpl). Settles ONE fact against the REAL RabbitMQ broker: on
/// this project's console messaging configuration
/// (<c>BaseConsole.Core.DependencyInjection.MessagingServiceCollectionExtensions.AddBaseConsoleMessaging</c>
/// — <c>AddMassTransit</c> → <c>UsingRabbitMq</c> → <c>Host</c> + four correlation filters +
/// <c>ConfigureEndpoints</c>, with NO <c>UseMessageRetry</c>, NO <c>ConfigureError</c>, NO
/// redelivery), when a consumer's <c>Consume</c> throws, does RabbitMQ redeliver the message to the
/// same queue (nack-requeue), or does MassTransit's DEFAULT error transport move it to
/// <c>&lt;queue&gt;_error</c> and ACK the original?
/// <para>
/// 28 doc-comment hits across 22 files in <c>src/</c> assert the first answer, reasoning from the
/// ABSENCE of a <c>ConfigureError</c> call. That inference is what this test replaces with evidence.
/// It closes the gap the suite itself admits at <c>Keeper/RecoveryDeadLetterFacts.cs</c>
/// ("broker-literal nack-requeue defers to the live proof").
/// </para>
/// <para>
/// The bus is built by the PRODUCTION <c>AddBaseConsoleMessaging</c> extension, byte-unchanged; the
/// only seam inputs are the consumer registration and the endpoint NAME. The host-mapped broker port
/// is reached through the unmodified <c>c.Host(rabbitHost, ...)</c> call site by supplying the
/// <c>RabbitMq:Host</c> config VALUE in URI-host form (<c>rabbitmq://localhost:5673/</c>) — a
/// documented overload behavior of <c>RabbitMqHostConfigurationExtensions.Host</c> in MassTransit 8.5.5.
/// </para>
/// <para>
/// Tagged <c>Category=RealStack</c> so the hermetic run (<c>Category!=RealStack</c>) excludes it. Not
/// placed in any collection: it touches neither Redis nor environment variables, builds its own
/// in-memory configuration, and its endpoint name is GUID-unique, so it cannot race anything.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
public sealed class ConsumerFaultDispositionE2ETests(ITestOutputHelper outputHelper)
{
    /// <summary>Host-mapped RabbitMQ management API (k8s port-forward). Basic auth guest:guest.</summary>
    private const string MgmtBaseUrl = "http://127.0.0.1:15673";

    /// <summary>Percent-encoded default vhost "/" for the management API path segment.</summary>
    private const string VHost = "%2F";

    /// <summary>Every probe queue carries this prefix — matches no production queue, and makes stray
    /// residue from an aborted run greppable and self-healing.</summary>
    private const string ProbeQueuePrefix = "skp-probe-fault-";

    /// <summary>Substring shared by the message-type exchange and the MassTransit fault exchange.</summary>
    private const string ProbeExchangeMarker = "ProbeFaultMessage";

    /// <summary>
    /// THE PIN. Set from OBSERVED reality, NEVER from expectation.
    /// <para>
    /// It was <see cref="FaultDisposition.Unpinned"/> until this probe was run against the real broker
    /// and its evidence block read. The test therefore COULD NOT pass before the measurement existed —
    /// which is the whole point: a test written to confirm a belief would have been green on day one
    /// and would have proven nothing.
    /// </para>
    /// <para>
    /// OBSERVED 2026-07-29, run id <c>a52a8e5c-c5b0-4531-bfb3-6e9943e6b018</c>, scratch queue
    /// <c>skp-probe-fault-a52a8e5cc5b04531bfb36e9943e6b018</c>, against MassTransit 8.5.5 on the
    /// Docker-Desktop k8s broker: the consumer was invoked <b>EXACTLY ONCE</b> (one offset, 3.09 s from
    /// recorder reset — no redelivery); <c>{scratch}_error</c> was ABSENT at the pre-send snapshot and
    /// APPEARED within the run, settling at <c>messages=1</c>; the scratch queue held
    /// <c>messages=0, messages_unacknowledged=0</c> at every snapshot including the final post-stop one,
    /// and <c>message_stats.redeliver</c> stayed 0 throughout. The original delivery was therefore ACKed
    /// and the body was moved by MassTransit's DEFAULT error transport — <b>not</b> nack-requeued.
    /// </para>
    /// <para>
    /// If this assertion ever fails after being pinned, the transport's fault handling has CHANGED and
    /// every "throw → nack-requeue" doc-comment in <c>src/</c> must be re-audited against the new reality.
    /// </para>
    /// <para>
    /// Held as a property (not a <c>const</c>) so the equality comparison below cannot be
    /// constant-folded into an always-false expression — which would be a BUILD FAILURE under
    /// <c>TreatWarningsAsErrors=true</c>.
    /// </para>
    /// </summary>
    private static FaultDisposition PinnedDisposition { get; } = FaultDisposition.ErrorTransportPark;

    [Fact]
    public async Task ThrowingConsumer_OnProductionConsoleBus_HasPinnedFaultDisposition()
    {
        var ct = TestContext.Current.CancellationToken;

        var runId = Guid.NewGuid();
        var scratch = $"{ProbeQueuePrefix}{runId:N}";

        ProbeFaultRecorder.Reset();

        using var admin = BuildAdminClient();

        var timeline = new List<Snapshot>();
        var evidence = "(evidence not produced — the run threw before the evidence block was built)";
        IHost? host = null;

        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // URI-host form: reaches the host-mapped port THROUGH the unmodified production
                // c.Host(rabbitHost, ...) call site with ZERO source change. This is a config VALUE,
                // exactly like the k8s manifests set RabbitMq__Host=rabbitmq.
                ["RabbitMq:Host"] = "rabbitmq://localhost:5673/",
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest",
            });

            // THE THING UNDER TEST — one production call, no configureBus seam, no hand-rolled
            // UsingRabbitMq, no AddBaseConsole/AddBaseConsoleObservability.
            builder.Services.AddBaseConsoleMessaging(
                builder.Configuration,
                x => x.AddConsumer<ProbeFaultConsumer>().Endpoint(e =>
                {
                    e.Name = scratch;
                    e.PrefetchCount = 1;          // rate hygiene against the LIVE broker
                    e.ConcurrentMessageLimit = 1; // one delivery in flight — keeps the timeline readable
                }));

            host = builder.Build();
            await host.StartAsync(ct);

            // The scratch queue APPEARING on localhost:15673 is the proof the bus reached the intended
            // broker on the intended port through the URI-host config value.
            var appeared = await WaitForQueueAsync(admin, scratch, TimeSpan.FromSeconds(20), ct);
            if (!appeared)
            {
                Assert.Fail(
                    $"scratch queue '{scratch}' never appeared on {MgmtBaseUrl} vhost / within 20 s. " +
                    "SUSPECT: the URI-host form 'rabbitmq://localhost:5673/' supplied as the RabbitMq:Host " +
                    "config value did not resolve to the host-mapped broker port through the production " +
                    "c.Host(rabbitHost, ...) call site. Measuring nothing is not a result — STOP and " +
                    "re-route via the configureBus host-override fallback before drawing any conclusion.");
            }

            // BEFORE snapshot: the error queue MUST NOT exist yet. Its later appearance is only
            // meaningful because this snapshot SHOWS it was absent (rather than assuming laziness).
            var before = await SnapshotAsync(admin, scratch, "pre-send", 0d, ct);
            timeline.Add(before);

            // Send exactly ONE message straight to the scratch endpoint. Send, NOT Publish: a Send to
            // queue:{scratch} targets that endpoint's own exchange and cannot fan out anywhere else.
            var bus = host.Services.GetRequiredService<IBus>();
            var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{scratch}"));
            await endpoint.Send(new ProbeFaultMessage(runId), ct);

            // ---- observation loop: ~500 ms cadence, hard 25 s cap, early break on an unambiguous answer
            var loopClock = Stopwatch.StartNew();
            var breakReason = "hard 25 s cap reached";
            while (loopClock.Elapsed < TimeSpan.FromSeconds(25))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                var snap = await SnapshotAsync(
                    admin, scratch, "observe", loopClock.Elapsed.TotalSeconds, ct);
                timeline.Add(snap);

                if (snap.Invocations >= 5)
                {
                    breakReason = "invocations >= 5 (redelivery is happening — stop hammering the live broker)";
                    break;
                }

                if (snap.ErrorExists && snap.ErrorMessages >= 1)
                {
                    breakReason = "error queue exists with messages >= 1 (the message has been parked)";
                    break;
                }
            }

            // Settle: capture an "N attempts THEN park" sequence whole rather than truncated at the break.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            timeline.Add(await SnapshotAsync(
                admin, scratch, "settle", loopClock.Elapsed.TotalSeconds, ct));

            await host.StopAsync(ct);

            // The management API refreshes queue statistics on a ~5 s interval; give it one full
            // interval after the bus is down so the FINAL messages / messages_unacknowledged pair
            // (the ACK evidence) is not read stale.
            await Task.Delay(TimeSpan.FromSeconds(6), ct);
            var final = await SnapshotAsync(
                admin, scratch, "final(bus stopped)", loopClock.Elapsed.TotalSeconds, ct);
            timeline.Add(final);

            var observed = ComputeDisposition(timeline, final);
            evidence = BuildEvidence(runId, scratch, timeline, breakReason, observed);

            outputHelper.WriteLine(evidence);

            Assert.True(
                observed == PinnedDisposition,
                $"fault disposition changed: pinned={PinnedDisposition}, observed={observed}.\n{evidence}");
        }
        finally
        {
            if (host is not null)
            {
                try
                {
                    await host.StopAsync(CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // best-effort second stop — the primary stop already ran on the happy path
                }
                host.Dispose();
            }

            await CleanupScratchTopologyAsync(admin, scratch);
            await AssertNoResidueAsync(admin);
        }
    }

    /// <summary>
    /// THE SECOND PIN — the RUNTIME-connected endpoint path. Set from OBSERVED reality, never expectation.
    /// <para>
    /// SCOPE GAP THIS CLOSES: the pin above measures a consumer bound STATICALLY, through
    /// <c>AddBaseConsoleMessaging</c>'s unconditional <c>ConfigureEndpoints(ctx)</c>. The PROCESSOR does
    /// not bind that way — it registers with <c>.ExcludeFromConfigureEndpoints()</c>
    /// (<c>BaseProcessorServiceCollectionExtensions.cs:89</c>/<c>:95</c>) and binds at RUNTIME via
    /// <c>IReceiveEndpointConnector.ConnectReceiveEndpoint</c>
    /// (<c>ProcessorStartupOrchestrator.cs:268</c>/<c>:285</c>). A claim that the processor REDELIVERS on
    /// throw therefore cannot be settled by a measurement of the static path — it needs this one.
    /// </para>
    /// <para>
    /// Started <see cref="FaultDisposition.Unpinned"/> and was pinned only after the first run FAILED with
    /// its evidence block read. Same discipline as the static pin — a test written to confirm either
    /// belief would be worthless.
    /// </para>
    /// <para>
    /// OBSERVED 2026-07-29, run id <c>c462c9c3-928a-4544-9285-26e10a1585dc</c>, scratch queue
    /// <c>skp-probe-fault-c462c9c3928a4544928526e10a1585dc</c>: <b>IDENTICAL to the static path.</b>
    /// Consumer invoked EXACTLY ONCE; break reason "error queue exists with messages >= 1 (the message has
    /// been parked)"; computed disposition <c>ErrorTransportPark</c>. The runtime
    /// <c>ConnectReceiveEndpoint</c> construction path therefore does NOT differ from the static
    /// <c>ConfigureEndpoints</c> path in fault disposition — the processor's endpoints park on throw too.
    /// </para>
    /// </summary>
    private static FaultDisposition PinnedRuntimeDisposition { get; } = FaultDisposition.ErrorTransportPark;

    [Fact]
    public async Task ThrowingConsumer_OnRuntimeConnectedEndpoint_HasPinnedFaultDisposition()
    {
        var ct = TestContext.Current.CancellationToken;

        var runId = Guid.NewGuid();
        var scratch = $"{ProbeQueuePrefix}{runId:N}";

        ProbeFaultRecorder.Reset();

        using var admin = BuildAdminClient();

        var timeline = new List<Snapshot>();
        var evidence = "(evidence not produced — the run threw before the evidence block was built)";
        IHost? host = null;

        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "rabbitmq://localhost:5673/",
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest",
            });

            // THE DIFFERENCE FROM THE STATIC PIN: registered but EXCLUDED from ConfigureEndpoints, exactly
            // as BaseProcessorServiceCollectionExtensions.cs:89 does, so no auto-named endpoint is created
            // and the ONLY bind is the runtime one below.
            builder.Services.AddBaseConsoleMessaging(
                builder.Configuration,
                x => x.AddConsumer<ProbeFaultConsumer>().ExcludeFromConfigureEndpoints());

            host = builder.Build();
            await host.StartAsync(ct);

            // Runtime bind — the processor's exact construction path (ProcessorStartupOrchestrator.cs:268).
            // Configure NOTHING but the consumer plus rate hygiene: no UseMessageRetry, no ConfigureError —
            // the same "bare tail" posture the processor's own bind uses.
            var connector = host.Services.GetRequiredService<IReceiveEndpointConnector>();
            var handle = connector.ConnectReceiveEndpoint(scratch, (rctx, cfg) =>
            {
                cfg.PrefetchCount = 1;
                cfg.ConcurrentMessageLimit = 1;
                cfg.ConfigureConsumer<ProbeFaultConsumer>(rctx);
            });
            await handle.Ready;

            var appeared = await WaitForQueueAsync(admin, scratch, TimeSpan.FromSeconds(20), ct);
            if (!appeared)
            {
                Assert.Fail(
                    $"scratch queue '{scratch}' never appeared on {MgmtBaseUrl} vhost / within 20 s — the " +
                    "runtime ConnectReceiveEndpoint bind did not reach the intended broker. Measuring " +
                    "nothing is not a result; STOP rather than drawing any conclusion.");
            }

            var before = await SnapshotAsync(admin, scratch, "pre-send", 0d, ct);
            timeline.Add(before);

            var bus = host.Services.GetRequiredService<IBus>();
            var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{scratch}"));
            await endpoint.Send(new ProbeFaultMessage(runId), ct);

            var loopClock = Stopwatch.StartNew();
            var breakReason = "hard 25 s cap reached";
            while (loopClock.Elapsed < TimeSpan.FromSeconds(25))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                var snap = await SnapshotAsync(
                    admin, scratch, "observe", loopClock.Elapsed.TotalSeconds, ct);
                timeline.Add(snap);

                if (snap.Invocations >= 5)
                {
                    breakReason = "invocations >= 5 (redelivery is happening — stop hammering the live broker)";
                    break;
                }

                if (snap.ErrorExists && snap.ErrorMessages >= 1)
                {
                    breakReason = "error queue exists with messages >= 1 (the message has been parked)";
                    break;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            timeline.Add(await SnapshotAsync(
                admin, scratch, "settle", loopClock.Elapsed.TotalSeconds, ct));

            await host.StopAsync(ct);

            await Task.Delay(TimeSpan.FromSeconds(6), ct);
            var final = await SnapshotAsync(
                admin, scratch, "final(bus stopped)", loopClock.Elapsed.TotalSeconds, ct);
            timeline.Add(final);

            // SAME discriminator as the static pin — deliberately shared and unedited, so the two paths are
            // scored by identical rules and any difference between them is a real difference in behavior.
            var observed = ComputeDisposition(timeline, final);
            evidence = BuildEvidence(runId, scratch, timeline, breakReason, observed);

            outputHelper.WriteLine("BIND MODE     : RUNTIME ConnectReceiveEndpoint (processor path)");
            outputHelper.WriteLine(evidence);

            Assert.True(
                observed == PinnedRuntimeDisposition,
                $"runtime-endpoint fault disposition: pinned={PinnedRuntimeDisposition}, observed={observed}.\n{evidence}");
        }
        finally
        {
            if (host is not null)
            {
                try
                {
                    await host.StopAsync(CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // best-effort second stop — the primary stop already ran on the happy path
                }
                host.Dispose();
            }

            await CleanupScratchTopologyAsync(admin, scratch);
            await AssertNoResidueAsync(admin);
        }
    }

    // ------------------------------------------------------------------ verdict discriminator

    /// <summary>
    /// Maps observations onto a <see cref="FaultDisposition"/>. Written BEFORE the first run and
    /// NEVER edited afterwards — rewriting the discriminator to match the outcome would destroy the
    /// only property that makes this proof worth anything (T-kpl-04).
    /// </summary>
    private static FaultDisposition ComputeDisposition(IReadOnlyList<Snapshot> timeline, Snapshot final)
    {
        var invocations = final.Invocations;
        var errorAppeared = timeline.Any(s => s.ErrorExists);
        var errorMaxMessages = timeline.Count == 0 ? 0L : timeline.Max(s => s.ErrorMessages);
        var scratchDrained = final is { ScratchMessages: 0, ScratchUnacked: 0 };

        if (invocations == 1 && errorAppeared && errorMaxMessages >= 1 && scratchDrained)
        {
            // The original was ACKed and the body moved — the src/ doc-comments would be WRONG.
            return FaultDisposition.ErrorTransportPark;
        }

        if (invocations >= 5 && !errorAppeared)
        {
            // The broker kept redelivering — the src/ doc-comments would be RIGHT.
            return FaultDisposition.NackRequeueRedelivery;
        }

        if (invocations > 1 && errorAppeared && errorMaxMessages >= 1)
        {
            return FaultDisposition.BoundedRetryThenPark;
        }

        return FaultDisposition.Inconclusive;
    }

    private static string BuildEvidence(
        Guid runId,
        string scratch,
        IReadOnlyList<Snapshot> timeline,
        string breakReason,
        FaultDisposition observed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== CONSUMER-FAULT DISPOSITION PROBE — RAW EVIDENCE ===");
        sb.AppendLine($"run id        : {runId:D}");
        sb.AppendLine($"scratch queue : {scratch}");
        sb.AppendLine($"bus built by  : BaseConsole.Core AddBaseConsoleMessaging (production, unmodified)");
        sb.AppendLine($"broker        : rabbitmq://localhost:5673/  (mgmt {MgmtBaseUrl})");
        sb.AppendLine($"break reason  : {breakReason}");
        sb.AppendLine();
        sb.AppendLine("label               |  elapsed |  inv | scratch.msgs | scratch.unack | scratch.redeliver | error? | error.msgs");
        sb.AppendLine("--------------------+----------+------+--------------+---------------+-------------------+--------+-----------");
        foreach (var s in timeline)
        {
            sb.AppendLine(
                $"{s.Label,-19} | {s.ElapsedSeconds,7:F2}s | {s.Invocations,4} | " +
                $"{(s.ScratchExists ? s.ScratchMessages.ToString() : "-"),12} | " +
                $"{(s.ScratchExists ? s.ScratchUnacked.ToString() : "-"),13} | " +
                $"{(s.ScratchExists ? s.ScratchRedeliver.ToString() : "-"),17} | " +
                $"{(s.ErrorExists ? "YES" : "no"),6} | {s.ErrorMessages,10}");
        }
        sb.AppendLine();
        sb.AppendLine($"total invocations : {ProbeFaultRecorder.Invocations}");
        sb.AppendLine(
            "invocation offsets: " +
            (ProbeFaultRecorder.Offsets.Count == 0
                ? "(none)"
                : string.Join(", ", ProbeFaultRecorder.Offsets.Select(o => $"{o.TotalSeconds:F2}s"))));
        sb.AppendLine();
        sb.AppendLine($"COMPUTED DISPOSITION : {observed}");
        // NOTE: the STATIC pin, printed for reference. The runtime-endpoint fact asserts against
        // PinnedRuntimeDisposition and prints its own BIND MODE line above this block — do not read this
        // line as that fact's expectation.
        sb.AppendLine($"PINNED DISPOSITION (static path) : {PinnedDisposition}");
        sb.AppendLine("=== END EVIDENCE ===");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ observation

    /// <summary>One observation row: the invocation count plus the broker's view of both queues.</summary>
    private sealed record Snapshot(
        string Label,
        double ElapsedSeconds,
        int Invocations,
        bool ScratchExists,
        long ScratchMessages,
        long ScratchUnacked,
        long ScratchRedeliver,
        bool ErrorExists,
        long ErrorMessages);

    private static async Task<Snapshot> SnapshotAsync(
        HttpClient admin, string scratch, string label, double elapsedSeconds, CancellationToken ct)
    {
        var queues = await ListQueuesAsync(admin, ct);
        var s = queues.FirstOrDefault(q => string.Equals(q.Name, scratch, StringComparison.Ordinal));
        var e = queues.FirstOrDefault(q => string.Equals(q.Name, scratch + "_error", StringComparison.Ordinal));

        return new Snapshot(
            Label: label,
            ElapsedSeconds: elapsedSeconds,
            Invocations: ProbeFaultRecorder.Invocations,
            ScratchExists: s is not null,
            ScratchMessages: s?.Messages ?? 0,
            ScratchUnacked: s?.Unacked ?? 0,
            ScratchRedeliver: s?.Redeliver ?? 0,
            ErrorExists: e is not null,
            ErrorMessages: e?.Messages ?? 0);
    }

    private static async Task<bool> WaitForQueueAsync(
        HttpClient admin, string name, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            var queues = await ListQueuesAsync(admin, ct);
            if (queues.Any(q => string.Equals(q.Name, name, StringComparison.Ordinal)))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        return false;
    }

    // ------------------------------------------------------------------ cleanup (ALLOW-LIST ONLY)

    /// <summary>
    /// Best-effort, unconditional, per-item cleanup of the probe's OWN topology.
    /// <para>
    /// STRICT ALLOW-LIST (T-kpl-01): the three <c>{scratch}*</c> queues and exchanges, any queue whose
    /// name starts with <c>skp-probe-fault-</c> (the self-healing sweep that reclaims residue from an
    /// aborted earlier run), and any exchange whose name contains <c>ProbeFaultMessage</c>. NO name-set
    /// diffing and NO "delete what appeared during the run" heuristic — the live stack creates and
    /// destroys queues on its own, and a wrong DELETE here destroys production topology. The bus's own
    /// temporary <c>bus-*</c> endpoint queue is auto-delete and is deliberately left alone.
    /// </para>
    /// </summary>
    private static async Task CleanupScratchTopologyAsync(HttpClient admin, string scratch)
    {
        var ct = CancellationToken.None;

        foreach (var suffix in new[] { "", "_error", "_skipped" })
        {
            await DeleteAsync(admin, "queues", scratch + suffix, ct);
            await DeleteAsync(admin, "exchanges", scratch + suffix, ct);
        }

        var queues = await ListQueuesAsync(admin, ct);
        foreach (var q in queues.Where(q => q.Name.StartsWith(ProbeQueuePrefix, StringComparison.Ordinal)))
        {
            await DeleteAsync(admin, "queues", q.Name, ct);
        }

        var exchanges = await ListExchangeNamesAsync(admin, ct);
        foreach (var name in exchanges.Where(n =>
                     n.Contains(ProbeExchangeMarker, StringComparison.Ordinal) ||
                     n.StartsWith(ProbeQueuePrefix, StringComparison.Ordinal)))
        {
            await DeleteAsync(admin, "exchanges", name, ct);
        }
    }

    /// <summary>Makes "the broker was left clean" a CHECKED FACT rather than an intention.</summary>
    private static async Task AssertNoResidueAsync(HttpClient admin)
    {
        var ct = CancellationToken.None;

        var queues = (await ListQueuesAsync(admin, ct))
            .Select(q => q.Name)
            .Where(n => n.StartsWith(ProbeQueuePrefix, StringComparison.Ordinal) ||
                        n.Contains(ProbeExchangeMarker, StringComparison.Ordinal))
            .ToList();

        var exchanges = (await ListExchangeNamesAsync(admin, ct))
            .Where(n => n.StartsWith(ProbeQueuePrefix, StringComparison.Ordinal) ||
                        n.Contains(ProbeExchangeMarker, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            queues.Count == 0 && exchanges.Count == 0,
            $"probe residue left on the broker — queues: [{string.Join(", ", queues)}], " +
            $"exchanges: [{string.Join(", ", exchanges)}]");
    }

    // ------------------------------------------------------------------ management API

    private static HttpClient BuildAdminClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(MgmtBaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest")));
        return http;
    }

    private sealed record QueueRow(string Name, long Messages, long Unacked, long Redeliver);

    private static async Task<IReadOnlyList<QueueRow>> ListQueuesAsync(HttpClient admin, CancellationToken ct)
    {
        using var resp = await admin.GetAsync(new Uri($"/api/queues/{VHost}", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var rows = new List<QueueRow>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            rows.Add(new QueueRow(
                el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                ReadLong(el, "messages"),
                ReadLong(el, "messages_unacknowledged"),
                el.TryGetProperty("message_stats", out var stats) && stats.ValueKind == JsonValueKind.Object
                    ? ReadLong(stats, "redeliver")
                    : 0));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<string>> ListExchangeNamesAsync(HttpClient admin, CancellationToken ct)
    {
        using var resp = await admin.GetAsync(new Uri($"/api/exchanges/{VHost}", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(el => el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty)
            .Where(n => n.Length > 0)
            .ToList();
    }

    /// <summary>DELETE one named entity. 204 AND 404 both count as success; anything else is swallowed
    /// (cleanup is best-effort per item, and <see cref="AssertNoResidueAsync"/> is the real gate).</summary>
    private static async Task DeleteAsync(HttpClient admin, string kind, string name, CancellationToken ct)
    {
        try
        {
            using var resp = await admin.DeleteAsync(
                new Uri($"/api/{kind}/{VHost}/{Uri.EscapeDataString(name)}", UriKind.Relative), ct);
            _ = resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound;
        }
        catch (HttpRequestException)
        {
            // best-effort per item
        }
        catch (TaskCanceledException)
        {
            // best-effort per item
        }
    }

    private static long ReadLong(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;
}
