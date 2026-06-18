namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// The OBS-03 Prometheus counter read-set as WINDOWED DELTAS (after − before, or
/// <c>increase(metric[window])</c>) — NOT raw cumulative. Prometheus counters are
/// process-lifetime cumulative and reset to zero on a process restart (and the proof crashes
/// processes deliberately), so reconciliation is only meaningful on a windowed delta over the
/// test interval (66-RESEARCH.md Pitfall 3 / A3). The fixture (Plan 03) computes the deltas from
/// two scrapes; the engine reconciles them.
///
/// <para>
/// <b>EXCEPTION — two windowEnd-CUMULATIVE fields.</b> <see cref="OrchestratorMessagesConsumedAtEnd"/>
/// and <see cref="ProcessorMessagesSentAtEnd"/> are NOT deltas — they are the windowEnd-CUMULATIVE
/// counter values. MG-1 conservation compares them directly (not the windowed delta) because the
/// force-recreate corrupts the windowStart delta baseline, leaving the windowEnd cumulative as the only
/// valid conservation measure.
/// </para>
///
/// <para>
/// A PURE DTO — no Prom/Http dependency. Synthetic snapshots drive the hermetic facts; real
/// scrapes feed the same shape from the fixture.
/// </para>
///
/// <para>
/// <b>Phase 74 uniform two-counter model.</b> The four LIVE deltas are the new
/// <c>{service}_messages_consumed</c> / <c>{service}_messages_sent</c> counters (D-12/D-13). The
/// legacy per-type names (<c>orchestrator_dispatch_sent</c>, <c>orchestrator_result_consumed</c>,
/// <c>processor_dispatch_consumed</c>, <c>processor_result_sent{outcome=…}</c>) and the three removed
/// dedup/drop counters (<c>orchestrator_result_deduped</c>, <c>processor_dispatch_deduped</c>,
/// <c>keeper_reinject_dropped</c>) plus the <c>outcome</c>-keyed breakdown are GONE — they emit no
/// series and have no field here.
/// </para>
/// </summary>
public sealed record PromCounterSnapshot
{
    /// <summary>orchestrator_messages_sent_total — windowed delta. The trigger denominator cross-check (D-13).</summary>
    public required double OrchestratorMessagesSentDelta { get; init; }

    /// <summary>orchestrator_messages_consumed_total — windowed delta (D-13).</summary>
    public required double OrchestratorMessagesConsumedDelta { get; init; }

    /// <summary>processor_messages_consumed_total — windowed delta (D-13).</summary>
    public required double ProcessorMessagesConsumedDelta { get; init; }

    /// <summary>
    /// processor_messages_sent_total — windowed delta (D-12: repointed from the old
    /// <c>processor_result_sent{outcome="completed"}</c>; the close-gate fixture is all-complete so
    /// total == completed and Expected = COMPLETE runs × 9 still holds; the <c>outcome</c> label is gone).
    /// </summary>
    public required double ProcessorMessagesSentDelta { get; init; }

    /// <summary>orchestrator_messages_consumed_total CUMULATIVE value at windowEnd (NOT a delta). MG-1
    /// conservation is checked on the windowEnd-cumulative pair, not the windowed delta, because the
    /// harness --force-recreate leaves stale pre-recreate series in Prometheus's ~5-min lookback that
    /// inflate the windowStart-pinned sum and corrupt the delta; the windowEnd read is clean (old series
    /// age out by then).</summary>
    public required double OrchestratorMessagesConsumedAtEnd { get; init; }

    /// <summary>processor_messages_sent_total CUMULATIVE value at windowEnd (NOT a delta). The MG-1 pair
    /// with <see cref="OrchestratorMessagesConsumedAtEnd"/>; see that field for why windowEnd-cumulative.</summary>
    public required double ProcessorMessagesSentAtEnd { get; init; }

    /// <summary>keeper_messages_consumed_total — windowed delta. &gt; 0 proves the keeper consumed recovery work.</summary>
    public required double KeeperMessagesConsumedDelta { get; init; }

    /// <summary>keeper_messages_sent_total — windowed delta. &gt; 0 proves the keeper re-emitted recovery messages.</summary>
    public required double KeeperMessagesSentDelta { get; init; }

    /// <summary>rate(keeper_l2_probe_total[…]) — the BIT probe cadence. &gt; 0 proves the keeper is live and probing L2.</summary>
    public required double KeeperL2ProbeRate { get; init; }
}
