namespace Keeper;

/// <summary>D-06: recovery-consumer knobs, bound from the "Recovery" appsettings section
/// (mirrors <see cref="ProbeOptions"/>). <see cref="PartitionCount"/> is the MassTransit UsePartitioner
/// slot count (D-06, default 8) — the only surviving knob now that the keeper-recovery endpoint is
/// symmetric with the exec path (no bus retry / no error transport, so no exhaustion-policy choice).
/// Phase 52 (D-09) REMOVED the obsolete <c>GateWaitSeconds</c> — gating now happens at the endpoint
/// (pause/resume), not via a bounded in-Consume await.</summary>
public sealed class RecoveryOptions
{
    public int PartitionCount { get; set; } = 8;    // D-06 default

    /// <summary>Phase 70 (req 7/11): the L2[messageId] output-blob TTL floor in seconds (default 300 —
    /// matches the processor's <c>ProcessorLivenessOptions.ExecutionDataTtlSeconds</c> default). INJECT
    /// writes <c>L2[messageId]=data</c> with the jittered <c>random[ExecutionDataTtl, 2×ExecutionDataTtl]</c>
    /// TTL — identical policy to the Pre/Post inline tail, so a keeper-completed result carries the same
    /// bounded lifetime as a directly-completed one. Bound from the "Recovery" section (no new Configure
    /// call — RecoveryOptions is already registered).</summary>
    public int ExecutionDataTtlSeconds { get; set; } = 300;
}
