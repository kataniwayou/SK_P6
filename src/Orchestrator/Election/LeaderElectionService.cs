using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orchestrator.Election;

/// <summary>
/// Hosts the Kubernetes leader election for the orchestrator (SPEC HA-02): a
/// <see cref="BackgroundService"/> that runs the <c>KubernetesClient</c> <see cref="LeaderElector"/>
/// over the <c>coordination.k8s.io/v1</c> Lease <c>skp/orchestrator-leader</c> and is the SOLE
/// writer of <see cref="LeaderState"/> (HA-03) via the election callbacks.
///
/// <para>
/// <b>Build-only in Phase 82 (D-06):</b> this service is wired ONLY in Program.cs and only when the
/// orchestrator runs in-cluster (Plan 02, D-02); it is NEVER started by hermetic tests. The tests
/// drive the same transitions the callbacks drive by calling <see cref="LeaderState"/> directly and
/// assert the fixed timings via the public constants below — no <c>IKubernetes</c> stub, no live
/// election. The live election is proven in Phase 83.
/// </para>
///
/// <para>
/// <b>Self-demotion fence (HA-04):</b> the fixed timings satisfy
/// <see cref="RenewDeadline"/> &lt; <see cref="LeaseDuration"/> — a demoted leader closes its gate
/// (via <c>OnStoppedLeading → BecomeFollower</c>) within <see cref="RenewDeadline"/>. Do NOT invert.
/// </para>
/// </summary>
public sealed class LeaderElectionService(
    LeaderState state,
    ILogger<LeaderElectionService> logger) : BackgroundService
{
    /// <summary>Lease duration — the leader must renew within this window or lose the lease.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(15);

    /// <summary>Renew deadline — the self-demotion fence; MUST be &lt; <see cref="LeaseDuration"/> (HA-04).</summary>
    public static readonly TimeSpan RenewDeadline = TimeSpan.FromSeconds(10);

    /// <summary>Retry period — how often a follower probes for an acquirable lease.</summary>
    public static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(2);

    /// <summary>The namespace holding the coordination Lease.</summary>
    public const string LeaseNamespace = "skp";

    /// <summary>The coordination Lease name the replicas contend for.</summary>
    public const string LeaseName = "orchestrator-leader";

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Lease-holder identity: the pod name in-cluster (unique per replica), else the machine name.
        var identity = Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;

        // Registered in-cluster only (Plan 02, D-02), so the mounted ServiceAccount token is present
        // and InClusterConfig() is valid here (T-82-01: least-privilege, lease-only SA per Plan 03 RBAC).
        IKubernetes kubernetes = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());

        // Hard-coded namespace + lease name — no dynamic/untrusted input feeds the lease coordinates.
        var leaseLock = new LeaseLock(kubernetes, LeaseNamespace, LeaseName, identity);

        var config = new LeaderElectionConfig(leaseLock)
        {
            LeaseDuration = LeaseDuration,
            RenewDeadline = RenewDeadline, // < LeaseDuration — self-demotion fence (HA-04); do NOT invert
            RetryPeriod = RetryPeriod,
        };

        using var elector = new LeaderElector(config);

        // The election callbacks are the SOLE LeaderState writer (HA-03 / D-06).
        elector.OnStartedLeading += () =>
        {
            state.BecomeLeader();
            logger.LogInformation("Acquired leadership; role=leader (identity {Identity})", identity);
        };
        elector.OnStoppedLeading += () =>
        {
            state.BecomeFollower();
            logger.LogInformation("Lost leadership; role=follower (identity {Identity})", identity);
        };
        elector.OnNewLeader += id => state.SetLeaderId(id);

        try
        {
            await elector.RunAndTryToHoldLeadershipForeverAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return; // shutdown requested — clean stop (mirrors HydrationBackgroundService)
        }
    }
}
