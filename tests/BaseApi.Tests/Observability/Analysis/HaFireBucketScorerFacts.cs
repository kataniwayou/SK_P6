using Xunit;

namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// Hermetic regression facts for <see cref="HaFireBucketScorer"/> (Phase 83 / HA-07). These pin the pure
/// 30s-bucket + gap + recovery fold that the live <c>HaFailoverAnalyzerE2ETests</c> verdict (Plan 02) will
/// delegate to — so the load-bearing decision logic is provable Docker-less, in-process, in seconds, before
/// any live harness exercises it against real Elasticsearch.
///
/// <para>
/// Each fact feeds synthetic <see cref="SendEvidenceRecord"/>s (the <c>(CorrelationId, @timestamp)</c> pairs
/// the live fixture derives from <c>exists attributes.StepId</c> hits) plus a synthetic <c>killUtc</c> +
/// <c>roleFlipUtc</c> into <see cref="HaFireBucketScorer.Score"/> and asserts the folded
/// <see cref="Verdict"/>. They mirror the <see cref="StructuralCohortFacts"/> idiom (synthetic record structs
/// over fixed constants) and — like that class — carry NO <c>Category=RealStack</c> trait, so they run under
/// the standard hermetic filter (<c>--filter-not-trait Category=RealStack</c>).
/// </para>
///
/// <para>
/// The six facts pin the ANTI-VACUOUS contract (the T-83-04a mitigation): a verdict of
/// <see cref="Verdict.Pass"/> requires POSITIVE evidence on every claim (a real gap tick, a bounded role
/// flip, per-tick uniqueness); observability gaps (no gap tick observed, zero send records) fold to
/// <see cref="Verdict.Inconclusive"/>, NEVER a false green; genuine findings (split-brain duplicate,
/// backfilled gap tick, recovery over the 2×LeaseDuration bound) fold to <see cref="Verdict.Fail"/>.
/// </para>
/// </summary>
public sealed class HaFireBucketScorerFacts
{
    // A 30s-aligned base instant (unix 1_700_000_040 is divisible by 30) so At(0)/At(30)/At(60)/At(90) floor
    // to distinct, predictable 30s buckets B0/B1/B2/B3. Mirrors StructuralCohortFacts' fixed-T0 discipline.
    private static readonly DateTimeOffset Base = DateTimeOffset.FromUnixTimeSeconds(1_700_000_040);

    // seconds-offset helper: At(5) → B0, At(35) → B1, At(65) → B2, At(95) → B3.
    private static DateTimeOffset At(int seconds) => Base.AddSeconds(seconds);

    // Synthetic send-evidence builder (the live shape: one correlationId per leader fire, earliest @timestamp).
    private static SendEvidenceRecord Send(string corr, DateTimeOffset ts) => new(corr, ts);

    /// <summary>
    /// The happy path (phase-aligned kill): one clean pre-kill fire (B0), one EMPTY election-gap tick (B1 —
    /// the skipped boundary tick), one clean post-recovery fire (B2), and a role flip 12s after the kill
    /// (≤ 2×LeaseDuration). Every bucket holds ≤1 distinct send-correlationId, the gap is observed and stays
    /// empty, recovery is bounded ⇒ <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    public void PhaseAlignedKill_OneGapBucket_AllUnique_Pass()
    {
        var sends = new[]
        {
            Send("corr-pre",  At(5)),   // B0 — clean pre-kill leader fire
            // B1 (At(30)..At(59)) — the election-gap tick, deliberately EMPTY (no fire)
            Send("corr-post", At(65)),  // B2 — clean post-recovery leader fire
        };

        var v = HaFireBucketScorer.Score(sends, killUtc: At(35), roleFlipUtc: At(47));

        Assert.True(v.ZeroDuplicate);
        Assert.True(v.GapObserved);
        Assert.True(v.NotBackfilled);
        Assert.True(v.RoleFlipVisible);
        Assert.True(v.BoundedRecovery);
        Assert.Equal(TimeSpan.FromSeconds(12), v.Recovery);
        Assert.Equal(Verdict.Pass, v.Verdict);
    }

    /// <summary>
    /// Split-brain / duplicate trigger: two DISTINCT send-correlationIds whose earliest @timestamps floor to
    /// the SAME 30s bucket (B0) — the operational signature of two simultaneous leaders firing one scheduled
    /// tick ⇒ <see cref="Verdict.Fail"/> (<see cref="HaFireVerdict.ZeroDuplicate"/> false), the fail-closed
    /// term that trips FIRST.
    /// </summary>
    [Fact]
    public void SplitBrain_TwoCorrIds_OneBucket_TripsDuplicate()
    {
        var sends = new[]
        {
            Send("corr-a", At(5)),   // B0
            Send("corr-b", At(10)),  // B0 — a SECOND distinct fire-correlationId in the same 30s bucket
        };

        var v = HaFireBucketScorer.Score(sends, killUtc: At(35), roleFlipUtc: At(47));

        Assert.False(v.ZeroDuplicate);
        Assert.Equal(Verdict.Fail, v.Verdict);
    }

    /// <summary>
    /// Coverage miss (Pitfall 1): recovery was fast enough (kill and role flip inside one 30s bucket) that NO
    /// scheduled tick landed in the election gap — every bucket holds exactly one corrId and there is no empty
    /// gap boundary between the last pre-kill fire and the first post-recovery fire. Claim #3 is UNOBSERVED, so
    /// the verdict must be <see cref="Verdict.Inconclusive"/> — never a vacuous <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    public void NoGapBucketObserved_IsInconclusive_NotPass()
    {
        var sends = new[]
        {
            Send("corr-pre",  At(5)),   // B0
            Send("corr-mid",  At(45)),  // B1 — the election-window tick FIRED (no skip): recovery beat the tick
            Send("corr-post", At(65)),  // B2
        };

        var v = HaFireBucketScorer.Score(sends, killUtc: At(35), roleFlipUtc: At(40));

        Assert.True(v.ZeroDuplicate);
        Assert.False(v.GapObserved);
        Assert.Equal(Verdict.Inconclusive, v.Verdict);
    }

    /// <summary>
    /// Backfilled gap tick: the election window spans two ticks (kill in B1, role flip in B2) — B1 is skipped
    /// (empty gap) but B2 later receives a send-correlationId (the survivor backfilled the missed tick, which
    /// the skip-on-gap design forbids). A clean post-recovery fire follows in B3. Gap IS observed but did NOT
    /// stay empty ⇒ <see cref="Verdict.Fail"/> (<see cref="HaFireVerdict.NotBackfilled"/> false).
    /// </summary>
    [Fact]
    public void BackfilledGapTick_IsFail()
    {
        var sends = new[]
        {
            Send("corr-pre",      At(5)),   // B0 — clean pre-kill fire
            // B1 (election tick) skipped — EMPTY gap
            Send("corr-backfill", At(65)),  // B2 — a fire landed on the second election tick (BACKFILL)
            Send("corr-post",     At(95)),  // B3 — clean post-recovery fire
        };

        // role flip 29s after the kill (bounded), but two election ticks: B1 empty, B2 backfilled.
        var v = HaFireBucketScorer.Score(sends, killUtc: At(35), roleFlipUtc: At(64));

        Assert.True(v.ZeroDuplicate);
        Assert.True(v.GapObserved);
        Assert.False(v.NotBackfilled);
        Assert.Equal(Verdict.Fail, v.Verdict);
    }

    /// <summary>
    /// Recovery over the bound: a valid single-corrId-per-bucket run with an observed empty gap, but the role
    /// flip lands 31s after the kill — beyond the 2×LeaseDuration (30s) recovery threshold ⇒
    /// <see cref="Verdict.Fail"/> (<see cref="HaFireVerdict.BoundedRecovery"/> false). A real finding: the
    /// survivor took too long to re-establish leadership.
    /// </summary>
    [Fact]
    public void RecoveryOver30s_IsFail()
    {
        var sends = new[]
        {
            Send("corr-pre",  At(5)),   // B0
            // B1, B2 skipped (empty gap across the slow election)
            Send("corr-post", At(95)),  // B3 — first post-recovery fire
        };

        var v = HaFireBucketScorer.Score(sends, killUtc: At(35), roleFlipUtc: At(66)); // 31s recovery

        Assert.True(v.ZeroDuplicate);
        Assert.True(v.GapObserved);
        Assert.True(v.RoleFlipVisible);
        Assert.False(v.BoundedRecovery);
        Assert.Equal(TimeSpan.FromSeconds(31), v.Recovery);
        Assert.Equal(Verdict.Fail, v.Verdict);
    }

    /// <summary>
    /// Observability-blind: zero send records reached the analyzer (no <c>exists attributes.StepId</c> hits in
    /// the window) ⇒ <see cref="Verdict.Inconclusive"/>, the fail-closed anti-vacuous rule (an empty window can
    /// never be a green). Trips the FIRST fold branch, before any bucket/gap/recovery reasoning.
    /// </summary>
    [Fact]
    public void ZeroSendRecords_IsInconclusive()
    {
        var v = HaFireBucketScorer.Score(Array.Empty<SendEvidenceRecord>(), killUtc: At(35), roleFlipUtc: At(47));

        Assert.Equal(Verdict.Inconclusive, v.Verdict);
    }
}
