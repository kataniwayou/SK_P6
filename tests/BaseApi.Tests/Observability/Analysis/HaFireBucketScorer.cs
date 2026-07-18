using System.Text;

namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// One parsed leader-fire send-evidence record (Phase 83 / HA-07) — the pure, <c>JsonElement</c>-decoupled
/// unit both the live fixture (<c>HaFailoverAnalyzerE2ETests</c>, Plan 02, which parses <c>exists
/// attributes.StepId</c> hits) and the hermetic <see cref="HaFireBucketScorerFacts"/> feed into
/// <see cref="HaFireBucketScorer.Score"/>. The <see cref="CorrelationId"/> is the per-fire correlationId
/// (<c>WorkflowFireJob</c> mints exactly one per scheduled fire, shared across the DAG); the
/// <see cref="Timestamp"/> is the record's <c>@timestamp</c> — the scorer takes each correlationId's EARLIEST
/// timestamp as the fire time (≈ Step_A, closest to the true tick), per RESEARCH Pitfall 2.
/// </summary>
internal readonly record struct SendEvidenceRecord(string CorrelationId, DateTimeOffset Timestamp);

/// <summary>
/// The immutable outcome of <see cref="HaFireBucketScorer.Score"/> — the five HA-07 claims folded into a
/// single <see cref="Verdict"/> plus the per-flag evidence the live report (Plan 02) serializes and the
/// <c>Resolve-AnalyzerExitCode</c> lib (Plan 03) maps to a 0/1/2 exit code. Mirrors the
/// <see cref="StructuralCohortResult"/> immutable-record idiom.
/// </summary>
internal sealed record HaFireVerdict
{
    /// <summary>The folded verdict (reuses the <see cref="Verdict"/> enum — serializes as its string name).</summary>
    public required Verdict Verdict { get; init; }

    /// <summary>Claim #1+#2: every 30s bucket held ≤1 distinct send-correlationId (no split-brain / duplicate trigger).</summary>
    public required bool ZeroDuplicate { get; init; }

    /// <summary>Claim #3 (half): ≥1 contiguous EMPTY election-gap tick was observed between the last pre-kill and the first post-recovery fire.</summary>
    public required bool GapObserved { get; init; }

    /// <summary>Claim #3 (half): no election-gap tick was later backfilled with a fire (skip-on-gap held).</summary>
    public required bool NotBackfilled { get; init; }

    /// <summary>Claim #5: an <c>attributes.role=leader</c> record appeared after the kill (the survivor's follower→leader flip).</summary>
    public required bool RoleFlipVisible { get; init; }

    /// <summary>Claim #4: the role flip landed within 2×LeaseDuration (30s) of the kill.</summary>
    public required bool BoundedRecovery { get; init; }

    /// <summary>The measured recovery delta (roleFlipUtc − killUtc), or null when no role flip was observed.</summary>
    public TimeSpan? Recovery { get; init; }

    /// <summary>Human-readable per-flag reasoning, so a red report is self-explaining.</summary>
    public required string HumanSummary { get; init; }
}

/// <summary>
/// The pure 30s-bucket + gap + recovery scorer (Phase 83 / HA-07 core). Folds the five HA-07 claims — per-tick
/// send uniqueness (#1+#2), election-gap skip-not-backfilled (#3), bounded recovery (#4), role transition
/// visible (#5) — into one <see cref="HaFireVerdict"/>. Isolated as a pure static classifier (mirroring
/// <see cref="StructuralCohort"/>) so the identical decision logic is provable Docker-less via
/// <see cref="HaFireBucketScorerFacts"/> before the live harness/verdict (Plans 02-04) exercise it against
/// real Elasticsearch.
///
/// <para>
/// <b>Fail-closed, anti-vacuous fold (T-83-04a mitigation):</b> zero send records OR no gap tick observed
/// (coverage miss) OR a negative recovery delta (clock skew / leaked pre-kill record) fold to
/// <see cref="Verdict.Inconclusive"/> — NEVER a false <see cref="Verdict.Pass"/>. A duplicate per bucket, a
/// backfilled gap tick, a recovery over 30s, or an absent role flip fold to <see cref="Verdict.Fail"/>.
/// </para>
/// </summary>
internal static class HaFireBucketScorer
{
    /// <summary>The bounded-recovery threshold: 2×LeaseDuration (LeaseDuration=15s), per D-06.</summary>
    private static readonly TimeSpan RecoveryBound = TimeSpan.FromSeconds(30);

    /// <summary>Floor a timestamp to its 30s wall-clock cron boundary (the <c>*/30 * * * * *</c> tick, RESEARCH Code Example 3).</summary>
    internal static long Bucket30s(DateTimeOffset ts) => ts.ToUnixTimeSeconds() - (ts.ToUnixTimeSeconds() % 30);

    /// <summary>
    /// Score a window of leader send-evidence records against the kill + role-flip instants. See the class
    /// summary for the fail-closed fold contract.
    /// </summary>
    /// <param name="sends">The <c>exists attributes.StepId</c> leader-fire records in the observation window (one correlationId per real fire).</param>
    /// <param name="killUtc">The wall-clock instant the leader pod was force-deleted (KILL_UTC, pinned at kubectl return).</param>
    /// <param name="roleFlipUtc">The earliest <c>attributes.role=leader</c> @timestamp after the kill (the survivor's flip), or null if none observed.</param>
    internal static HaFireVerdict Score(
        IReadOnlyList<SendEvidenceRecord> sends,
        DateTimeOffset killUtc,
        DateTimeOffset? roleFlipUtc)
    {
        // ── Claim #1 + #2 (RESEARCH Code Example 3): each correlationId's EARLIEST @timestamp is its fire
        // time; floor to the 30s wall-clock boundary; assert ≤1 distinct send-correlationId per bucket.
        var fireTimeByCorr = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var r in sends)
        {
            if (!fireTimeByCorr.TryGetValue(r.CorrelationId, out var cur) || r.Timestamp < cur)
            {
                fireTimeByCorr[r.CorrelationId] = r.Timestamp;
            }
        }

        var byBucket = fireTimeByCorr
            .GroupBy(kv => Bucket30s(kv.Value))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Key).Distinct().Count());

        bool zeroDuplicate = byBucket.Values.All(distinct => distinct <= 1);

        // ── Claim #4 + #5 (RESEARCH Code Example 5): the role flip is the single source of truth for both
        // recovery-bound and role-transition-visible.
        bool roleFlipVisible = roleFlipUtc is not null;
        TimeSpan? recovery = roleFlipUtc is { } rf ? rf - killUtc : null;
        bool boundedRecovery = recovery is { } rec && rec >= TimeSpan.Zero && rec <= RecoveryBound;

        // ── Claim #3 (RESEARCH Code Example 4 intent): walk the election window [killBucket, recoveryBucket]
        // forward from the last pre-kill fire, collecting the contiguous EMPTY gap ticks. A skipped tick that
        // is later refilled (a fire on an election tick AFTER a gap tick) is a backfill (skip-on-gap violated).
        //
        // NOTE (deviation from the verbatim Code Example 4 arithmetic): computing the gap boundaries as those
        // strictly between the closest fire buckets around killBucket makes `notBackfilled` tautologically true
        // (no fire bucket can lie between the two nearest fire buckets), which makes the required
        // BackfilledGapTick_IsFail fold unreachable. This election-window walk preserves the same semantics
        // (contiguous empty ticks between last-pre-kill and first-post-recovery, overlapping [KILL_UTC,
        // roleFlipUtc]) while keeping the backfill Fail path genuinely reachable.
        long killBucket = Bucket30s(killUtc);
        long recoveryBucket = roleFlipUtc is { } rfb ? Bucket30s(rfb) : killBucket;
        long lastPreKill = byBucket.Keys.Where(b => b < killBucket).DefaultIfEmpty(long.MinValue).Max();

        var gapBoundaries = new List<long>();
        bool backfillSeen = false;
        if (lastPreKill != long.MinValue)
        {
            long horizon = Math.Max(recoveryBucket, killBucket) + 30L;
            for (long b = lastPreKill + 30L; b <= horizon; b += 30L)
            {
                bool inElection = b >= killBucket && b <= recoveryBucket;
                bool filled = byBucket.ContainsKey(b);

                if (!inElection)
                {
                    if (filled) break; // the first clean fire past the election window closes the scan
                    continue;
                }

                if (filled)
                {
                    // A fire on an election tick is a backfill only if a gap tick was already skipped before it;
                    // an election tick that simply fired (no prior gap) is a fast recovery, not a backfill.
                    if (gapBoundaries.Count > 0)
                    {
                        backfillSeen = true;
                    }
                }
                else
                {
                    gapBoundaries.Add(b); // an empty (skipped) election-gap tick
                }
            }
        }

        // gapBoundaries holds only empty in-election ticks, so their presence already satisfies the
        // anti-vacuous overlap guard (each falls within [killBucket, recoveryBucket]).
        bool gapObserved = gapBoundaries.Count >= 1;
        bool notBackfilled = !backfillSeen;

        // ── Fail-closed fold (exact order — Inconclusive-first so empty/ambiguous data never mis-Fails).
        Verdict verdict;
        string reason;
        if (sends.Count == 0)
        {
            verdict = Verdict.Inconclusive;
            reason = "no send-evidence records in window (observability-blind — never a vacuous Pass)";
        }
        else if (!zeroDuplicate)
        {
            verdict = Verdict.Fail;
            reason = "≥2 distinct send-correlationIds in one 30s bucket (split-brain / duplicate trigger)";
        }
        else if (!roleFlipVisible)
        {
            verdict = Verdict.Fail;
            reason = "no attributes.role=leader record after the kill (role transition not visible)";
        }
        else if (recovery is { } neg && neg < TimeSpan.Zero)
        {
            verdict = Verdict.Inconclusive;
            reason = $"negative recovery delta ({neg}) — clock skew or a leaked pre-kill role record";
        }
        else if (recovery is { } over && over > RecoveryBound)
        {
            verdict = Verdict.Fail;
            reason = $"recovery {over} exceeds the 2×LeaseDuration bound ({RecoveryBound})";
        }
        else if (gapObserved && !notBackfilled)
        {
            verdict = Verdict.Fail;
            reason = "an election-gap tick was backfilled with a fire (skip-on-gap violated)";
        }
        else if (!gapObserved)
        {
            verdict = Verdict.Inconclusive;
            reason = "no election-gap tick observed (coverage miss — kill missed the gap; never a vacuous Pass)";
        }
        else
        {
            verdict = Verdict.Pass;
            reason = "all five HA-07 claims green: ≤1 corrId/bucket, one empty non-backfilled gap tick, bounded role flip";
        }

        var summary = new StringBuilder()
            .Append("HA-07 verdict=").Append(verdict).Append(": ").Append(reason).Append(". ")
            .Append("[zeroDuplicate=").Append(zeroDuplicate)
            .Append(", gapObserved=").Append(gapObserved)
            .Append(", notBackfilled=").Append(notBackfilled)
            .Append(", roleFlipVisible=").Append(roleFlipVisible)
            .Append(", boundedRecovery=").Append(boundedRecovery)
            .Append(", recovery=").Append(recovery?.ToString() ?? "n/a")
            .Append(", buckets=").Append(byBucket.Count)
            .Append(", gapTicks=").Append(gapBoundaries.Count)
            .Append(']')
            .ToString();

        return new HaFireVerdict
        {
            Verdict = verdict,
            ZeroDuplicate = zeroDuplicate,
            GapObserved = gapObserved,
            NotBackfilled = notBackfilled,
            RoleFlipVisible = roleFlipVisible,
            BoundedRecovery = boundedRecovery,
            Recovery = recovery,
            HumanSummary = summary,
        };
    }
}
