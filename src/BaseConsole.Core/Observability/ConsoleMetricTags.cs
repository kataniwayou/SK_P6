namespace BaseConsole.Core.Observability;

/// <summary>
/// The cross-service metric TAG names shared by every console tier that emits a business counter —
/// orchestrator, keeper and the processor framework (the three assemblies that reference
/// <c>BaseConsole.Core</c>). Declaring them ONCE here is what makes a PromQL join on
/// <c>processorId</c>/<c>workflowId</c> reach all three tiers: the tag name is no longer a bare string
/// literal repeated at ~13 independent increment sites across three assemblies.
/// <para>
/// WHY THIS EXISTS: <c>processor_spawn_dropped</c> was once tagged PascalCase <c>"ProcessorId"</c> while
/// every sibling counter used camelCase <c>"processorId"</c>. Nothing failed loudly — the counter simply
/// fell out of every query joining on the camelCase name, and the drift survived until it was spotted in
/// live Prometheus label output. A per-site literal cannot be kept honest by review; a const can.
/// </para>
/// <para>
/// CONVENTION — metrics tag camelCase, logs attribute PascalCase. The PascalCase twins live in
/// <c>Messaging.Contracts.ExecutionLogScope</c> (<c>ProcessorId</c>/<c>WorkflowId</c>) and are DELIBERATELY
/// different strings: the log keys must equal their structured-parameter names so they surface at
/// <c>attributes.&lt;Key&gt;</c> in Elasticsearch, while the metric tags follow OTel's camelCase attribute
/// convention. The two must never be interchanged — <c>ProcessorMetricsFacts</c> pins them apart.
/// </para>
/// <para>
/// NOT here: <c>identityName</c>. It is processor-only (emitted at the four
/// <c>BaseProcessor.Core</c> sites and built from <c>IProcessorContext</c>, a type this assembly does not
/// know), so it stays on <c>BaseProcessor.Core.Observability.ProcessorMetrics.IdentityNameTag</c>.
/// </para>
/// </summary>
public static class ConsoleMetricTags
{
    /// <summary>camelCase tag carrying the owning workflow id. Emitted by orchestrator, keeper and processor.</summary>
    public const string WorkflowIdTag = "workflowId";

    /// <summary>camelCase tag carrying the processor id the datapoint is attributed to. Emitted by
    /// orchestrator, keeper and processor — the join key that ties a hop across all three tiers.</summary>
    public const string ProcessorIdTag = "processorId";
}
