namespace Orchestrator.Configuration;

/// <summary>D-17 (Phase 71): the orchestrator-side <c>L2[data:messageId]</c> output-blob TTL floor in
/// seconds (default 300 — matching <c>ProcessorLivenessOptions.ExecutionDataTtlSeconds</c> /
/// <c>RecoveryOptions.ExecutionDataTtlSeconds</c>). The orchestrator <c>RelocateTail</c> (Post path) writes
/// <c>L2[data:messageId]=Data</c> with the jittered <c>random[OutputDataTtlSeconds, 2×OutputDataTtlSeconds]</c>
/// TTL supplied by <c>L2ProjectionKeys.OutputDataTtl</c> — the single source of truth for the policy. This
/// only supplies the floor from the bound "Orchestrator" config section.</summary>
public sealed class OrchestratorOutputOptions
{
    public int OutputDataTtlSeconds { get; set; } = 300;
}
