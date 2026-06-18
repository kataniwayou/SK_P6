namespace BaseApi.Tests.Observability;

/// <summary>
/// Single source of truth for the Prometheus series names the live-stack analyzer and the ops
/// harness query. Phase 74 renamed the orchestrator dispatch counter and updated the analyzer but
/// NOT the harness, which silently aborted the whole fault suite. Centralising the names here lets
/// HarnessMetricDriftFacts assert the harness script and the analyzer agree. The collector appends
/// the Prometheus `_total` suffix, so these carry it (the src instrument names do not).
/// </summary>
public static class LiveMetricNames
{
    public const string OrchestratorMessagesSentTotal = "orchestrator_messages_sent_total";
    public const string OrchestratorMessagesConsumedTotal = "orchestrator_messages_consumed_total";
    public const string ProcessorMessagesConsumedTotal = "processor_messages_consumed_total";
    public const string ProcessorMessagesSentTotal = "processor_messages_sent_total";
    public const string KeeperMessagesConsumedTotal = "keeper_messages_consumed_total";
    public const string KeeperMessagesSentTotal = "keeper_messages_sent_total";
    public const string KeeperL2ProbeTotal = "keeper_l2_probe_total";
}
