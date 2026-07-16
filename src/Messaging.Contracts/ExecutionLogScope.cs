namespace Messaging.Contracts;

/// <summary>
/// Execution-id log-scope keys (LOG-03). Key strings MUST equal the structured-param names so a
/// value placed under a key here surfaces at the SAME Elasticsearch <c>attributes.&lt;Key&gt;</c>
/// field as the matching template parameter, via the existing OTel IncludeScopes+ParseStateValues
/// bridge. Sibling to <see cref="CorrelationKeys"/>; CorrelationId is deliberately NOT here — it
/// stays owned by <see cref="CorrelationKeys.LogScope"/> (D-01). Pure POCO leaf — no MassTransit ref.
/// </summary>
public static class ExecutionLogScope
{
    public const string WorkflowId  = "WorkflowId";
    public const string StepId      = "StepId";
    public const string ProcessorId = "ProcessorId";
    public const string ExecutionId = "ExecutionId";
    public const string EntryId     = "EntryId";

    /// <summary>
    /// Single source of truth for the 5-key execution-scope dict, shared by the bus-wide inbound
    /// consume filter and (next wave) the Keeper fault consumers that open the scope manually — the
    /// filter does NOT fire on <c>Fault&lt;T&gt;</c> (D-07). Byte-identical skip rules: each Guid is
    /// skipped when <c>Guid.Empty</c>, the Guid EntryId when it is the source-step sentinel
    /// (<see cref="SourceStep.IsSource"/>, D-07 — never inline <c>== Guid.Empty</c>); no CorrelationId key.
    /// <para>Both overloads share ONE skip-rule implementation: the <see cref="IExecutionCorrelated"/>
    /// form DELEGATES to the loose-id positional form. The Keeper recovery consumers hold the five ids
    /// but are NOT <see cref="IExecutionCorrelated"/>, so they call the positional overload directly
    /// (D2/LOG-02) to build the identical scope.</para>
    /// </summary>
    public static Dictionary<string, object> BuildState(IExecutionCorrelated ec)
        => BuildState(ec.WorkflowId, ec.StepId, ec.ProcessorId, ec.ExecutionId, ec.EntryId);

    /// <summary>Loose-id overload for callers that hold the five ids but are not IExecutionCorrelated
    /// (the Keeper recovery consumers — D2/LOG-02). Byte-identical skip rules: each Guid skipped when
    /// Guid.Empty; the EntryId skipped via SourceStep.IsSource (never inline == Guid.Empty, D-07).</summary>
    public static Dictionary<string, object> BuildState(
        Guid workflowId, Guid stepId, Guid processorId, Guid executionId, Guid entryId)
    {
        var state = new Dictionary<string, object>();
        if (workflowId  != Guid.Empty) state[WorkflowId]  = workflowId.ToString();
        if (stepId      != Guid.Empty) state[StepId]      = stepId.ToString();
        if (processorId != Guid.Empty) state[ProcessorId] = processorId.ToString();
        if (executionId != Guid.Empty) state[ExecutionId] = executionId.ToString();
        if (!SourceStep.IsSource(entryId)) state[EntryId] = entryId.ToString();
        return state;
    }
}
