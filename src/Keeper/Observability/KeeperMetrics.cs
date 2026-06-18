using System.Diagnostics.Metrics;

namespace Keeper.Observability;

/// <summary>D-07 — the Keeper's code-owned <see cref="Meter"/> ("Keeper") holding the recovery-state
/// observability instruments. Modeled EXACTLY on <c>BaseProcessor.Core.Observability.ProcessorMetrics</c>:
/// built via <see cref="IMeterFactory"/> (the .NET 8 blessed DI pattern) — NEVER a <c>static Meter</c>,
/// which would leak across tests in the shared hermetic process. <see cref="MeterName"/> is the single
/// const referenced by BOTH this <c>meterFactory.Create</c> call AND the
/// <c>ConfigureOpenTelemetryMeterProvider(mp =&gt; mp.AddMeter(KeeperMetrics.MeterName))</c> registration
/// (the meter is wired in Plan 02's Program.cs edit).
/// <para>
/// Phase 74 (REQ-3/REQ-4/REQ-6): the legacy label-less reinject-drop counter is
/// REMOVED and replaced by the uniform two-counter pair shared across the whole platform —
/// <c>keeper_messages_consumed</c> (incremented ONCE at the <c>RecoveryConsumerBase.Consume</c> choke point
/// all six recovery consumers funnel through) and <c>keeper_messages_sent</c> (incremented after every
/// successful outbound <c>ep.Send</c> across the four SENDING consumers via the shared
/// <c>RecoveryConsumerBase.CountSent</c> helper) — both labeled camelCase <c>workflowId</c>+<c>processorId</c>.
/// A third, label-less <c>keeper_l2_probe</c> heartbeat increments once per <c>BitHealthLoop</c> tick so
/// <c>rate(keeper_l2_probe_total[5m]) &gt; 0</c> proves the Keeper is actively probing L2.
/// </para>
/// <para>
/// Counter names are snake_case with NO Prometheus counter suffix: the collector's prometheus exporter
/// <c>add_metric_suffixes</c> default appends the <c>_total</c> suffix itself.
/// </para></summary>
public sealed class KeeperMetrics
{
    /// <summary>The meter name — MUST equal the <c>AddMeter("Keeper")</c> registration.</summary>
    public const string MeterName = "Keeper";

    /// <summary>REQ-3 — <c>keeper_messages_consumed</c>: incremented ONCE in
    /// <c>RecoveryConsumerBase.Consume</c> (the single choke point all six recovery consumers funnel
    /// through), labeled camelCase <c>workflowId</c>+<c>processorId</c> from <c>IKeeperRecoverable</c>.</summary>
    public Counter<long> MessagesConsumed { get; }

    /// <summary>REQ-3 — <c>keeper_messages_sent</c>: incremented after every successful outbound
    /// <c>ep.Send</c> across the four SENDING consumers via the shared <c>RecoveryConsumerBase.CountSent</c>
    /// helper (never on the Reinject drop/early-return path; the two Delete consumers send nothing).
    /// Labeled camelCase <c>workflowId</c>+<c>processorId</c>.</summary>
    public Counter<long> MessagesSent { get; }

    /// <summary>REQ-4 — <c>keeper_l2_probe</c>: a LABEL-LESS heartbeat incremented once per
    /// <c>BitHealthLoop</c> tick (one per <c>probe.ProbeOnceAsync</c> call, regardless of healthy/unhealthy
    /// result). Proves the Keeper is actively probing L2.</summary>
    public Counter<long> L2Probe { get; }

    public KeeperMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        MessagesConsumed = meter.CreateCounter<long>("keeper_messages_consumed");   // collector appends _total
        MessagesSent     = meter.CreateCounter<long>("keeper_messages_sent");       // collector appends _total
        L2Probe          = meter.CreateCounter<long>("keeper_l2_probe");            // collector appends _total
    }
}
