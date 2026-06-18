using System.Diagnostics.Metrics;

namespace Orchestrator.Observability;

/// <summary>
/// Phase 74 (REQ-1/REQ-6) — the Orchestrator's code-owned <see cref="Meter"/> ("Orchestrator") holding the
/// uniform two-counter business model (<c>orchestrator_messages_consumed</c> / <c>orchestrator_messages_sent</c>)
/// plus the kept <c>orchestrator_step_unresolved</c> counter.
/// <para>
/// Built via <see cref="IMeterFactory"/> (the .NET 8 blessed DI pattern, auto-registered by the
/// generic host) — NEVER a <c>static Meter</c> field, which would leak across tests in the shared
/// hermetic process. <see cref="MeterName"/> is the single const referenced by BOTH this
/// <c>meterFactory.Create</c> call AND the <c>ConfigureOpenTelemetryMeterProvider(mp =&gt;
/// mp.AddMeter(OrchestratorMetrics.MeterName))</c> registration in <c>Program.cs</c> (D-02 symmetry).
/// </para>
/// <para>
/// The counter names are snake_case with NO Prometheus counter suffix (D-03): the collector's
/// prometheus exporter <c>add_metric_suffixes</c> default appends the suffix itself, so embedding it
/// in the instrument name here would double it. The two business counters are tagged camelCase
/// <c>workflowId</c>+<c>processorId</c> (D-07) at the increment site (<c>messageId</c> is the counting
/// unit, never a label — D-04) and inherit the ambient <c>service_instance_id</c> resource label.
/// </para>
/// </summary>
public sealed class OrchestratorMetrics
{
    /// <summary>The meter name — MUST equal the <c>AddMeter("Orchestrator")</c> registration (D-02).</summary>
    public const string MeterName = "Orchestrator";

    /// <summary>
    /// <c>orchestrator_messages_consumed</c> (Phase 74, REQ-1/D-03) — incremented ONCE at the consume entry
    /// of <see cref="Consumers.TypedResultConsumer{T}"/>.Consume, one per consumed result. Tagged camelCase
    /// <c>workflowId</c>+<c>processorId</c> (D-07); <c>messageId</c> is the counting unit, NOT a label (D-04).
    /// </summary>
    public Counter<long> MessagesConsumed { get; }

    /// <summary>
    /// <c>orchestrator_messages_sent</c> (Phase 74, REQ-1/D-02) — incremented after EVERY successful outbound
    /// broker Send: forward dispatch (StepDispatcher), fan-out per match (OrchestratorPrePipeline), and the
    /// keeper REINJECT/DELETE/INJECT escalations (OrchestratorPrePipeline + RelocateTail SendKeeper). A
    /// failed/exhausted Send does NOT count (count-after-success). Tagged camelCase
    /// <c>workflowId</c>+<c>processorId</c> (D-05/D-06/D-07); <c>messageId</c> is NOT a label (D-04).
    /// </summary>
    public Counter<long> MessagesSent { get; }

    /// <summary>
    /// <c>orchestrator_step_unresolved</c> — incremented (Plan 03) at each L1-resolution miss: a dangling
    /// next-step id from <c>SelectNext</c>'s <c>UnresolvedIds</c>. Tagged <c>workflowId</c> only (label-minimal,
    /// bounded cardinality — T-72-05). snake_case, NO <c>_total</c> in the name (the collector appends it, D-03).
    /// </summary>
    public Counter<long> StepUnresolved { get; }

    public OrchestratorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        MessagesConsumed = meter.CreateCounter<long>("orchestrator_messages_consumed"); // Phase 74 D-03 — collector appends the suffix
        MessagesSent     = meter.CreateCounter<long>("orchestrator_messages_sent");     // Phase 74 D-02 — collector appends the suffix
        StepUnresolved   = meter.CreateCounter<long>("orchestrator_step_unresolved");   // Phase 72 D-03 — collector appends the suffix
    }
}
