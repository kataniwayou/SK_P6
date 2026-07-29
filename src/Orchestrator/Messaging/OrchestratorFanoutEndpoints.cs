namespace Orchestrator.Messaging;

/// <summary>
/// Single source of truth (SoT) for the orchestrator's two PER-REPLICA fan-out receive-endpoint
/// queue names. Each name is <c>{base}-{instanceId}</c> so every replica declares a DISTINCT
/// exclusive/<c>Temporary</c> (auto-delete) queue and none collide on <c>RESOURCE_LOCKED</c>.
/// <para>
/// <b>HA-07 root cause (fixed here):</b> the four fan-out <c>ConsumerDefinition</c>s used to pin a
/// literal <c>EndpointName</c> in their constructors while <c>Program.cs</c> asked for
/// <c>.Endpoint(e =&gt; { e.InstanceId = …; e.Temporary = true; })</c>. In MassTransit 8.5.5 a literal
/// <c>EndpointName</c> BYPASSES the <c>InstanceId</c>-aware name formatter, so the queues kept the
/// fixed base name + <c>Temporary=true</c> (exclusive) → at <c>replicas&gt;1</c> the 2nd/3rd replica
/// hit <c>RESOURCE_LOCKED</c>, the bus faulted, <c>/health/ready</c> returned 503, and the deployment
/// never reached 3/3. The literal <c>EndpointName</c> was removed from those definitions and
/// <c>Program.cs</c> now sets an explicit per-instance <c>e.Name</c> via <see cref="PerInstance"/>.
/// </para>
/// <para>
/// <b>Co-location contract:</b> each pair (Start+Stop, PauseAll+ResumeAll) resolves to the SAME
/// per-instance name for a given <c>instanceId</c>, keeping the global pause/resume pair's
/// <c>ConcurrentMessageLimit = 1</c> serialization intact. The shared competing-consumer
/// <c>orchestrator-result*</c> endpoints are NOT modeled here — they are stable, non-exclusive, and
/// unchanged.
/// </para>
/// </summary>
public static class OrchestratorFanoutEndpoints
{
    /// <summary>Base name for the Start + Stop lifecycle fan-out pair (co-located).</summary>
    public const string LifecycleBase = "orchestrator";

    /// <summary>Base name for the global PauseAll + ResumeAll fan-out pair (co-located).</summary>
    public const string GlobalPauseResumeBase = "orchestrator-global-pauseresume";

    /// <summary>
    /// The pure per-instance name helper: <c>{baseName}-{instanceId}</c>. Two different instanceIds
    /// yield distinct names (no exclusive-queue collision); the same base+instanceId always yields the
    /// same name (co-location preserved).
    /// </summary>
    public static string PerInstance(string baseName, string instanceId) => $"{baseName}-{instanceId}";
}
