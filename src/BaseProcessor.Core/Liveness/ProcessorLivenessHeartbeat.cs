using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaseProcessor.Core.Liveness;

/// <summary>
/// The only-when-Healthy liveness heartbeat (LIVE-01..06 / LOOP-02). A <see cref="BackgroundService"/>
/// that, every <see cref="ProcessorLivenessOptions.IntervalSeconds"/>, builds a FROZEN-Healthy
/// <see cref="ProcessorLivenessEntry"/> (all-SUCCESS summary => <see cref="LivenessStatus.Healthy"/>,
/// active interval = <see cref="ProcessorLivenessOptions.IntervalSeconds"/>/10) and routes it through the
/// SHARED <see cref="ProcessorLivenessWriter"/> (Plan 02) — so the L2 per-instance SET, the index SADD, the
/// L1 Update, and the derived TTL all come for free and match the startup writer (the two loops cannot drift).
///
/// <para>
/// <b>Healthy gate (LIVE-04 / D-14 / T-26-09 / T-60-08):</b> a beat writes ONLY when
/// <see cref="IProcessorContext.IsHealthy"/> and the Id is populated. The gate is the sole authorization
/// signal for the <c>healthy</c> write; a not-yet-Healthy replica no-ops the tick (writes nothing) so the
/// gate reader sees it as <c>absent</c>.
/// </para>
/// <para>
/// <b>Frozen-healthy (D-14 / WR-03 / T-60-11):</b> the beat does NOT re-read context definition props on the
/// heartbeat thread — it feeds a fixed all-SUCCESS summary into <see cref="ProcessorLivenessEntry.Create"/>,
/// which derives <see cref="LivenessStatus.Healthy"/>. No cross-thread stale read.
/// </para>
/// <para>
/// <b>Per-instance contract (D-05 / Pitfall 5 / T-60-10):</b> the OLD flat per-processor projection /
/// flat-liveness-key write is GONE — there is NO dual-write. The writer owns the
/// per-instance key (<c>skp:proc:{id}:{instanceId}</c>), its index SET, and the derived TTL
/// (<c>max(10*2, Ttl-floor) = 30</c>, D-13 / T-60-09 — the key always outlives the inter-beat gap).
/// </para>
/// <para>
/// <b>Resilience (D-11 / T-26-10):</b> a Redis fault on a beat is logged-and-continued here (belt-and-braces;
/// the writer also catches) — the host never crashes and the loop keeps beating.
/// </para>
/// <para>
/// <b>Shared loop-liveness (Phase 86 / HLTH-03 / T-86-14):</b> at the TOP of every iteration — BEFORE the
/// only-when-Healthy L2 gate — the loop calls <see cref="ILivenessHeartbeat.Beat"/> UNCONDITIONALLY and, on
/// the first iteration, <see cref="IStartupGate.MarkReady"/> once. So a not-yet-Healthy processor booting
/// against a down bus still stamps a fresh liveness instant each tick → the shared
/// <c>LoopLivenessHealthCheck</c> keeps <c>/health/live</c> Healthy → no false restart. The
/// <c>IsHealthy</c> gate now guards ONLY the L2 healthy write (below), never the beat.
/// </para>
/// </summary>
public sealed class ProcessorLivenessHeartbeat : BackgroundService
{
    private readonly ProcessorLivenessWriter _writer;
    private readonly IProcessorContext _context;
    private readonly ProcessorLivenessOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILivenessHeartbeat _heartbeat;
    private readonly IStartupGate _gate;
    private readonly ILogger<ProcessorLivenessHeartbeat> _logger;
    private readonly string _instanceId;

    public ProcessorLivenessHeartbeat(
        ProcessorLivenessWriter writer,
        IProcessorContext context,
        IOptions<ProcessorLivenessOptions> options,
        TimeProvider clock,
        ILivenessHeartbeat heartbeat,
        IStartupGate gate,
        string instanceId,
        ILogger<ProcessorLivenessHeartbeat> logger)
    {
        _writer  = writer ?? throw new ArgumentNullException(nameof(writer));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock   = clock ?? throw new ArgumentNullException(nameof(clock));
        // Phase 86 (HLTH-03): the shared loop-liveness holder the beat stamps + the one-shot startup gate the
        // first beat marks ready — both resolved from DI via the ActivatorUtilities registration.
        _heartbeat = heartbeat ?? throw new ArgumentNullException(nameof(heartbeat));
        _gate      = gate ?? throw new ArgumentNullException(nameof(gate));
        // KEY-03: the per-replica instance identity, resolved ONCE by the caller (InstanceId.Resolve() at
        // the DI site — available from boot) and passed in, so a test can pin a deterministic instanceId.
        _instanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
        _logger  = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(_options.IntervalSeconds);
        var firstBeat = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Phase 86 (HLTH-03 / T-86-14): beat UNCONDITIONALLY at the top of the loop, ABOVE the Healthy
            // gate — so a not-yet-Healthy replica (e.g. booting against a down bus) still stamps a fresh
            // liveness instant each tick and /health/live stays Healthy (no false restart). On the FIRST
            // iteration, mark the startup gate ready once (the loop is actually running).
            _heartbeat.Beat();
            if (firstBeat)
            {
                _gate.MarkReady();
                firstBeat = false;
            }

            // Healthy gate (LIVE-04 / D-14 / T-26-09) — only a Healthy, identified replica writes the
            // healthy value; a not-yet-Healthy replica no-ops the L2 WRITE this tick (it does NOT wait — a
            // no-op write is correct). This gate now guards ONLY the L2 write, never the beat above.
            if (_context.IsHealthy && _context.Id is { } id)
            {
                try
                {
                    // SAME clock the reader uses (Pitfall 2).
                    var now = _clock.GetUtcNow().UtcDateTime;

                    // Frozen healthy (D-14): all outcomes SUCCESS => Create derives Healthy. Does NOT
                    // re-read context definition props (WR-03 / T-60-11). Active interval = heartbeat
                    // IntervalSeconds (D-12) — baked into the entry so the writer derives TTL = max(10*2, 30).
                    var entry = ProcessorLivenessEntry.Create(
                        inputOutcome:  SchemaOutcome.Success,
                        outputOutcome: SchemaOutcome.Success,
                        configOutcome: SchemaOutcome.Success,
                        timestamp:     now,
                        interval:      _options.IntervalSeconds);

                    // Shared writer (Plan 02): SET(perInstance, ttl=max(20,30)=30) + idempotent SADD + L1 Update.
                    await _writer.WriteAsync(id, _instanceId, entry);
                }
                catch (Exception ex)
                {
                    // Resilience (D-11 / T-26-10): log-and-CONTINUE; never throw, never return. Belt-and-braces
                    // — the writer also catches, but a fault must never crash the host or stop the loop.
                    // WR-02: log the gate-captured local `id` (the exact value the failed write used),
                    // not the _context.Id property re-read — keeps the diagnostic and the write in lock-step.
                    _logger.LogWarning(
                        ex,
                        "Liveness heartbeat write failed for processor {ProcessorId}; will retry next beat",
                        id);
                }
            }

            try
            {
                await Task.Delay(period, _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
