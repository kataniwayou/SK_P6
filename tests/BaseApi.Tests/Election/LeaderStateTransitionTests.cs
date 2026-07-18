using Orchestrator.Election;
using Xunit;

namespace BaseApi.Tests.Election;

/// <summary>
/// HA-02/HA-03/HA-04 (Phase 82, D-06): drives the <see cref="LeaderState"/> single-writer snapshot and
/// the <see cref="LeaderElectionService"/> public timing constants DIRECTLY — NO IKubernetes stub, NO
/// started election service. The election callbacks (OnStartedLeading / OnStoppedLeading) are simulated
/// by invoking the SAME writer methods they call (<see cref="LeaderState.BecomeLeader"/> /
/// <see cref="LeaderState.BecomeFollower"/>), so the snapshot transition is proven without a live elector.
/// <list type="bullet">
///   <item>Default/unstarted state is follower (HA-02): the pre-acquisition gate is closed.</item>
///   <item>BecomeLeader → leader, BecomeFollower → follower (HA-03 single-writer flip; HA-04 self-demotion).</item>
///   <item>The off-cluster seed (<c>startAsLeader: true</c>, D-02) starts as leader.</item>
///   <item>RenewDeadline &lt; LeaseDuration (HA-04 self-demotion fence) with the exact 15/10/2s values.</item>
/// </list>
/// </summary>
public sealed class LeaderStateTransitionTests
{
    [Fact]
    public void Default_LeaderState_Is_Follower()
    {
        var s = new LeaderState();

        Assert.False(s.IsLeader);            // pre-acquisition: not leader (HA-02)
        Assert.Equal("follower", s.Role);    // role literal defaults to follower
    }

    [Fact]
    public void BecomeLeader_Then_BecomeFollower_Flips_Snapshot()
    {
        var s = new LeaderState();

        // Simulates OnStartedLeading — the sole leader writer (HA-03).
        s.BecomeLeader();
        Assert.True(s.IsLeader);
        Assert.Equal("leader", s.Role);

        // Simulates OnStoppedLeading — the HA-04 self-demotion gate-close.
        s.BecomeFollower();
        Assert.False(s.IsLeader);
        Assert.Equal("follower", s.Role);
    }

    [Fact]
    public void OffCluster_Seed_Starts_Leader()
    {
        // D-02: the off-cluster / test default seeds leader so a lone instance still fires.
        var s = new LeaderState(startAsLeader: true);

        Assert.True(s.IsLeader);
        Assert.Equal("leader", s.Role);
    }

    [Fact]
    public void RenewDeadline_Is_Less_Than_LeaseDuration()
    {
        // HA-04 self-demotion fence: a demoted leader must close its gate WITHIN RenewDeadline, which
        // MUST be strictly less than LeaseDuration. Assert the ordering AND the exact fixed values so an
        // accidental inversion (or a timing edit) is caught here without starting the election service.
        Assert.True(
            LeaderElectionService.RenewDeadline < LeaderElectionService.LeaseDuration,
            "RenewDeadline must be < LeaseDuration (HA-04 self-demotion fence)");

        Assert.Equal(TimeSpan.FromSeconds(15), LeaderElectionService.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), LeaderElectionService.RenewDeadline);
        Assert.Equal(TimeSpan.FromSeconds(2), LeaderElectionService.RetryPeriod);
    }
}
