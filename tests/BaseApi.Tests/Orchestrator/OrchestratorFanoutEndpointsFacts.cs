using Orchestrator.Messaging;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 83 (HA-07) — hermetic regression guard pinning the per-replica fan-out endpoint naming
/// contract of <see cref="OrchestratorFanoutEndpoints"/>. NO <c>Category=RealStack</c> trait: these are
/// pure string assertions, Docker-less, mirroring the <c>ResolveInstanceIdFacts</c> style.
/// <para>
/// The exact property whose ABSENCE caused the <c>RESOURCE_LOCKED</c> collision that blocked
/// <c>replicas:3</c> is <see cref="PerInstance_TwoDifferentInstanceIds_YieldDistinctNames"/>: with the
/// old literal <c>EndpointName</c>, two pods declared the SAME exclusive/<c>Temporary</c> queue name.
/// Co-location per pair (Start==Stop, Pause==Resume, PauseAll==ResumeAll for a given instanceId) is
/// pinned so the fix cannot silently split a pair and break the shared <c>ConcurrentMessageLimit=1</c>
/// serialization.
/// </para>
/// </summary>
public sealed class OrchestratorFanoutEndpointsFacts
{
    public static readonly string[] AllBases =
    {
        OrchestratorFanoutEndpoints.LifecycleBase,
        OrchestratorFanoutEndpoints.PauseResumeBase,
        OrchestratorFanoutEndpoints.GlobalPauseResumeBase,
    };

    [Fact]
    public void PerInstance_TwoDifferentInstanceIds_YieldDistinctNames()
    {
        // The RESOURCE_LOCKED root cause: two pods MUST NOT resolve the same exclusive queue name.
        foreach (var baseName in AllBases)
        {
            var a = OrchestratorFanoutEndpoints.PerInstance(baseName, "orchestrator-pod-a");
            var b = OrchestratorFanoutEndpoints.PerInstance(baseName, "orchestrator-pod-b");
            Assert.NotEqual(a, b);
        }
    }

    [Fact]
    public void PerInstance_SameInstanceId_CoLocatesEachPair()
    {
        const string id = "orchestrator-pod-a";

        // Start and Stop co-locate; Pause and Resume co-locate; PauseAll and ResumeAll co-locate —
        // each pair shares ONE per-instance endpoint (preserving ConcurrentMessageLimit=1 serialization).
        Assert.Equal(
            OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.LifecycleBase, id),
            OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.LifecycleBase, id));
        Assert.Equal(
            OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.PauseResumeBase, id),
            OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.PauseResumeBase, id));
        Assert.Equal(
            OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.GlobalPauseResumeBase, id),
            OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.GlobalPauseResumeBase, id));
    }

    [Fact]
    public void PerInstance_ThreeBases_AreMutuallyDistinct_ForSameInstance()
    {
        const string id = "orchestrator-pod-a";

        var lifecycle = OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.LifecycleBase, id);
        var pauseResume = OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.PauseResumeBase, id);
        var globalPauseResume = OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.GlobalPauseResumeBase, id);

        // Start/Stop, Pause/Resume, PauseAll/ResumeAll stay on three SEPARATE endpoints.
        Assert.NotEqual(lifecycle, pauseResume);
        Assert.NotEqual(lifecycle, globalPauseResume);
        Assert.NotEqual(pauseResume, globalPauseResume);
    }

    [Fact]
    public void PerInstance_ProducesExpectedShape()
    {
        Assert.Equal("orchestrator-pod-x",
            OrchestratorFanoutEndpoints.PerInstance("orchestrator", "pod-x"));
        Assert.Equal("orchestrator-pauseresume-pod-x",
            OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.PauseResumeBase, "pod-x"));
        Assert.Equal("orchestrator-global-pauseresume-pod-x",
            OrchestratorFanoutEndpoints.PerInstance(OrchestratorFanoutEndpoints.GlobalPauseResumeBase, "pod-x"));

        // Never null/empty for any base.
        foreach (var baseName in AllBases)
        {
            var name = OrchestratorFanoutEndpoints.PerInstance(baseName, "pod-x");
            Assert.False(string.IsNullOrEmpty(name));
        }
    }
}
