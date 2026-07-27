using System.Diagnostics.Metrics;

namespace BaseProcessor.Core.Observability;

/// <summary>
/// Phase 74 (REQ-2/REQ-6) — the processor framework's code-owned <see cref="Meter"/> ("BaseProcessor")
/// holding the uniform two-counter business model (<c>processor_messages_consumed</c> /
/// <c>processor_messages_sent</c>) plus the kept <c>processor_spawn_dropped</c> counter.
/// Owned by <c>BaseProcessor.Core</c> and inherited by EVERY concrete <c>Processor.*</c> (the meter is
/// registered inside <c>AddBaseProcessor</c>).
/// <para>
/// Built via <see cref="IMeterFactory"/> (the .NET 8 blessed DI pattern, auto-registered by the
/// generic host) — NEVER a <c>static Meter</c> field, which would leak across tests in the shared
/// hermetic process. <see cref="MeterName"/> is the single const referenced by BOTH this
/// <c>meterFactory.Create</c> call AND the <c>ConfigureOpenTelemetryMeterProvider(mp =&gt;
/// mp.AddMeter(ProcessorMetrics.MeterName))</c> registration inside <c>AddBaseProcessor</c> (D-02 symmetry).
/// </para>
/// <para>
/// The counter names are snake_case with NO Prometheus counter suffix (D-03): the collector's
/// prometheus exporter <c>add_metric_suffixes</c> default appends the suffix itself, so embedding it
/// in the instrument name here would double it. The two business counters are tagged camelCase
/// <c>workflowId</c>+<c>processorId</c> (D-07) at the increment site (<c>messageId</c> is the counting
/// unit, never a label — D-04; the Phase-32 <c>outcome</c> label is REMOVED this phase) and inherit the
/// ambient <c>service_instance_id</c> resource label from Plan 01.
/// </para>
/// </summary>
public sealed class ProcessorMetrics
{
    /// <summary>The meter name — MUST equal the <c>AddMeter("BaseProcessor")</c> registration (D-02).</summary>
    public const string MeterName = "BaseProcessor";

    /// <summary>
    /// <c>processor_messages_consumed</c> (Phase 74, REQ-2/D-03) — incremented ONCE at the consume entry of
    /// <see cref="Processing.EntryStepDispatchConsumer"/>.Consume, one per consumed dispatch. Tagged camelCase
    /// <c>workflowId</c>+<c>processorId</c> (D-07); <c>messageId</c> is the counting unit, NOT a label (D-04).
    /// </summary>
    public Counter<long> MessagesConsumed { get; }

    /// <summary>
    /// <c>processor_messages_sent</c> (Phase 74, REQ-2/D-02) — incremented AFTER the successful Step* send in
    /// <see cref="Processing.OutputTail"/>.SendResult (count-after-success; a failed/exhausted send does NOT
    /// count). Tagged camelCase <c>workflowId</c>+<c>processorId</c> (D-05/D-06/D-07); the Phase-32
    /// <c>outcome</c> label is REMOVED and <c>messageId</c> is NOT a label (D-04).
    /// </summary>
    public Counter<long> MessagesSent { get; }

    /// <summary>IN-03 (Phase 70): <c>processor_spawn_dropped</c> — incremented when a Mode-2
    /// <c>SpawnToPost</c> send EXHAUSTS the bounded RetryLoop and is swallowed (best-effort spawn; the
    /// scheduler re-fires the whole entry). Its OWN counter so the spawn-drop rate is observable and is not
    /// conflated with the unrelated dispatch signal. Tagged camelCase <c>processorId</c> at the increment
    /// site -- the SAME tag name the two business counters use (D-07), so all three join on one label.</summary>
    public Counter<long> SpawnDropped { get; }

    public ProcessorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        MessagesConsumed = meter.CreateCounter<long>("processor_messages_consumed"); // Phase 74 D-03 — collector appends the suffix
        MessagesSent     = meter.CreateCounter<long>("processor_messages_sent");     // Phase 74 D-02 — collector appends the suffix
        SpawnDropped     = meter.CreateCounter<long>("processor_spawn_dropped");     // IN-03 — collector appends the suffix
    }
}
