namespace Orchestrator.Election;

/// <summary>
/// Immutable leadership snapshot swapped as a whole record on every state transition
/// (mirrors the <c>wf.Liveness = current with { … }</c> immutable-record-swap idiom in
/// <c>WorkflowFireJob</c>). The record is never mutated in
/// place — a new instance is published to the <see cref="LeaderState"/> volatile field, so a
/// cross-thread reader always observes a fully-constructed, self-consistent triple.
/// </summary>
/// <param name="IsLeader">True iff this replica currently holds the lease.</param>
/// <param name="Role">The role literal — exactly <c>"leader"</c> or <c>"follower"</c>.</param>
/// <param name="CurrentLeaderId">The identity of the observed leader, or null before any is known.</param>
internal sealed record LeaderSnapshot(bool IsLeader, string Role, string? CurrentLeaderId);

/// <summary>
/// Single-writer, lock-free leadership snapshot holder for the orchestrator HA gate (SPEC HA-03).
///
/// <para>
/// The <c>_snapshot</c> field is a <c>volatile</c> reference: writes publish a brand-new immutable
/// <see cref="LeaderSnapshot"/> and reads observe it through the volatile field, so there is no
/// torn read and no lock. This is the exact discipline used by <c>IStartupGate</c>'s
/// backing latch (Volatile read/write), applied to a whole-record swap instead of an int.
/// </para>
///
/// <para>
/// <b>Single-writer invariant (HA-03):</b> after construction the <c>_snapshot</c> field is mutated
/// ONLY by the leader-election callbacks — via <see cref="BecomeLeader"/>,
/// <see cref="BecomeFollower"/>, and <see cref="SetLeaderId"/>, whose sole caller is
/// <c>LeaderElectionService</c>'s <c>OnStartedLeading</c> / <c>OnStoppedLeading</c> /
/// <c>OnNewLeader</c> wiring. No second writer exists — the fire gate (<c>WorkflowFireJob</c>) and
/// the role enricher only READ this state. The constructor may seed the leader state (the off-cluster
/// Plan-02 D-02 default), but that is construction, not a runtime write, so the invariant holds.
/// </para>
/// </summary>
public sealed class LeaderState
{
    private volatile LeaderSnapshot _snapshot;

    /// <summary>
    /// Creates the holder. In-cluster the default is the pre-acquisition follower state (SPEC HA-02);
    /// <paramref name="startAsLeader"/> is the off-cluster seed used where election never runs
    /// (Plan 02, D-02) — the constructor sets it, NOT a runtime writer, so the single-writer
    /// invariant is preserved.
    /// </summary>
    /// <param name="startAsLeader">When true, seed the snapshot as leader; otherwise follower (default).</param>
    public LeaderState(bool startAsLeader = false) =>
        _snapshot = startAsLeader
            ? new LeaderSnapshot(true, "leader", null)
            : new LeaderSnapshot(false, "follower", null);

    /// <summary>True iff this replica currently holds leadership.</summary>
    public bool IsLeader => _snapshot.IsLeader;

    /// <summary>The current role literal — <c>"leader"</c> or <c>"follower"</c>.</summary>
    public string Role => _snapshot.Role;

    /// <summary>The identity of the last-observed leader, or null before any is known.</summary>
    public string? CurrentLeaderId => _snapshot.CurrentLeaderId;

    /// <summary>
    /// Election-callback writer (HA-03): publish the leader snapshot, preserving the known leader id.
    /// Whole-record volatile swap — lock-free and torn-read-safe.
    /// </summary>
    public void BecomeLeader() =>
        _snapshot = new LeaderSnapshot(true, "leader", _snapshot.CurrentLeaderId);

    /// <summary>
    /// Election-callback writer (HA-03 / HA-04 self-demotion fence): publish the follower snapshot,
    /// closing the fire gate. Whole-record volatile swap — lock-free and torn-read-safe.
    /// </summary>
    public void BecomeFollower() =>
        _snapshot = new LeaderSnapshot(false, "follower", _snapshot.CurrentLeaderId);

    /// <summary>
    /// Election-callback writer (HA-03): record the observed leader identity, preserving the current
    /// leader/role. Whole-record volatile swap — lock-free and torn-read-safe.
    /// </summary>
    /// <param name="id">The observed leader identity, or null if unknown.</param>
    public void SetLeaderId(string? id)
    {
        var current = _snapshot;
        _snapshot = new LeaderSnapshot(current.IsLeader, current.Role, id);
    }
}
