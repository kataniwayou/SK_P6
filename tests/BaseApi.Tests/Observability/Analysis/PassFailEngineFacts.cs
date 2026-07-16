using Xunit;

namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// Hermetic unit facts for <see cref="PassFailEngine"/> — re-founded on the ES-binding model (67-03).
/// One fact per decision branch, proving the correctness arbiter is exercised (not vacuously green)
/// before any live stack runs (T-66-01, 66-VALIDATION.md). Every fact builds synthetic
/// <see cref="RunTrace"/> (via <see cref="RunTrace.FromLabels"/>) + a synthetic
/// <see cref="PromCounterSnapshot"/> and calls <c>new PassFailEngine().Analyze(...)</c>. No ES/Prom
/// client, no live stack, sub-second.
///
/// <para>
/// Fact → requirement map (67-03 ES-binding semantics):
/// <list type="bullet">
/// <item><c>Complete_AllStartedRuns_Yields_Pass</c> — ES-binding: every started run 10-label complete (Step_G ×2) → Pass.</item>
/// <item><c>Incomplete_StartedRun_DropsStepF2_Yields_Fail</c> — OBS-02: a started-but-incomplete run → Fail (binding).</item>
/// <item><c>Duplicate_TwoStepC_Yields_FailClosed</c> — OBS-02: an ILLEGITIMATE duplicate (non-convergent Step_C ×2) → Fail (binding fail-closed; Step_G ×2 stays legitimate).</item>
/// <item><c>RetiredConflation_ProcessorSentShort_DoesNotFailVerdict</c> — 67-03: ResultSentCompleted short no longer fails (retired #2).</item>
/// <item><c>RemovedDedupCounters_HaveNoSnapshotField_AbsenceProvenByCompile</c> — D-14: the 3 removed dedup/drop counters have no snapshot field (absence by compile).</item>
/// </list>
/// </para>
///
/// <para>
/// Each spawned execution is its own run, so traces are keyed per (correlationId, executionId);
/// <see cref="RunTrace.FromLabels"/> takes a distinct executionId per instance. A duplicate label WITHIN
/// one (correlationId, executionId) still drives the fail-closed duplicate branch.
/// </para>
/// </summary>
public sealed class PassFailEngineFacts
{
    /// <summary>
    /// The full 10-label completeness set as RAW labels for a COMPLETE run (73, D-10): Step_A…Step_F2 once
    /// PLUS the convergent terminal Step_G TWICE (the legitimate per-arrival fan-in). DISTINCT collapses to 10;
    /// the Step_G ×2 is exempt from the duplicate-fail via RunTrace.HasIllegitimateDuplicate. These facts pass
    /// NO value map, so the engine's value-chain check skips them (legacy-caller behaviour) and each fact stays
    /// focused on its ONE branch (completeness / duplicate / Prom corroboration). The dedicated value-chain
    /// Pass/Fail proofs live in PassFailEngineValueChainFacts.
    /// </summary>
    private static readonly string[] AllTenLabelsWithConvergentGx2 =
        { "Step_A", "Step_B", "Step_C", "Step_D1", "Step_E1", "Step_F1", "Step_D2", "Step_E2", "Step_F2", "Step_G", "Step_G" };

    // A clean snapshot that isolates the ES-binding branches (completeness / duplicate): result conservation
    // HOLDS (consumed == sent, |0−0| ≤ 1) and the keeper probe is LIVE (rate > 0) so the Phase-74 binding
    // metric gate passes and never confounds those branches. Keeper recovery deltas stay 0 (these facts pass
    // no expectsKeeperActivity → MG-2 expects 0). Probe rate is positive so MG-3 passes — a zero probe would
    // fail every Pass-asserting fact built on this snapshot.
    private static PromCounterSnapshot CleanSnapshot() => new()
    {
        OrchestratorMessagesSentDelta = 0,
        OrchestratorMessagesConsumedDelta = 0,
        ProcessorMessagesConsumedDelta = 0,
        ProcessorMessagesSentDelta = 0,
        OrchestratorMessagesConsumedAtEnd = 0,
        ProcessorMessagesSentAtEnd = 0,
        KeeperMessagesConsumedDelta = 0,
        KeeperMessagesSentDelta = 0,
        KeeperL2ProbeRate = 0.2,
    };

    [Fact]
    public void Complete_AllStartedRuns_Yields_Pass()
    {
        // 3 STARTED runs (distinct correlationIds), all 10-label complete (Step_G ×2 legitimate) → ES-binding Pass.
        var runs = new[]
        {
            RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2),
            RunTrace.FromLabels("corr-2", "exec-2", AllTenLabelsWithConvergentGx2),
            RunTrace.FromLabels("corr-3", "exec-3", AllTenLabelsWithConvergentGx2),
        };
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(runs, snap, "unit-test");

        Assert.Equal(3, report.StartedRuns);                                   // ES-binding denominator
        Assert.Equal(3, report.CompleteRuns);                                  // OBS-01
        Assert.Equal(0, report.Missing);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void Incomplete_StartedRun_DropsStepF2_Yields_Fail()
    {
        // The run STARTED (it logged Step_A…) but is missing the Step_F2 sink → 9 distinct labels → incomplete.
        // Step_G ×2 stays legitimate (not a duplicate); the ONLY failure driver is the missing Step_F2.
        var missingF2Labels = AllTenLabelsWithConvergentGx2.Where(l => l != "Step_F2").ToArray();
        var run = RunTrace.FromLabels("corr-1", "exec-1", missingF2Labels);
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");

        Assert.Equal(1, report.StartedRuns);     // it DID start (≥1 Step_* log)
        Assert.Equal(0, report.CompleteRuns);
        Assert.Equal(1, report.Missing);         // OBS-02: started-but-incomplete, bound to ES denominator
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEmpty(report.MissingDetail);
    }

    [Fact]
    public void Duplicate_TwoStepC_Yields_FailClosed()
    {
        // All 10 distinct labels PRESENT (incl. the legitimate Step_G ×2), but Step_C appears twice → an
        // ILLEGITIMATE (non-convergent) duplicate → HasIllegitimateDuplicate true → fail-closed.
        var labelsWithDuplicate = AllTenLabelsWithConvergentGx2.Concat(new[] { "Step_C" }).ToArray();
        // Duplicate WITHIN one (correlationId, executionId) instance → fail-closed.
        var run = RunTrace.FromLabels("corr-1", "exec-1", labelsWithDuplicate);
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");

        Assert.Equal(Verdict.Fail, report.Verdict);   // BINDING fail-closed even with every distinct label present
        Assert.NotEmpty(report.Duplicates);
    }

    [Fact]
    public void RetiredConflation_ProcessorSentShort_DoesNotFailVerdict()
    {
        // Retired conflation #2: under the OLD model the processor sent-completed counter < complete × 9
        // forced an Unreconciled FAIL. Under 67-03 that arithmetic is gone from the binding gate — a complete
        // ES-binding cohort PASSES regardless of the processor_messages_sent counter value.
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = CleanSnapshot() with
        {
            // short of the OLD complete×9 expectation — would have failed the RETIRED binding gate. Conservation
            // is kept BALANCED (orchestrator_consumed == processor_sent) so the NEW binding MG-1 gate is not
            // confounded: this fact isolates "the retired complete×9 counter no longer gates", not MG-1.
            OrchestratorMessagesConsumedDelta = 8,
            ProcessorMessagesSentDelta = 8,
        };

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");

        Assert.Equal(1, report.CompleteRuns);
        Assert.Equal(Verdict.Pass, report.Verdict); // ES-binding pass; the retired counter no longer gates
    }

    // A quiescent, conserving snapshot for N "results": result conservation holds exactly at drain.
    private static PromCounterSnapshot ConservingSnapshot(double results, double keeperConsumed = 0,
        double keeperSent = 0, double probeRate = 0.2) => new()
    {
        OrchestratorMessagesSentDelta = results * 2,            // ~2x results (two-consumer hop); not asserted
        OrchestratorMessagesConsumedDelta = results,           // == processor_sent at quiescence (MG-1)
        ProcessorMessagesConsumedDelta = results,
        ProcessorMessagesSentDelta = results,
        OrchestratorMessagesConsumedAtEnd = results,           // == processor_sent@end → MG-1 conservation holds
        ProcessorMessagesSentAtEnd = results,
        KeeperMessagesConsumedDelta = keeperConsumed,
        KeeperMessagesSentDelta = keeperSent,
        KeeperL2ProbeRate = probeRate,
    };

    [Fact]
    public void MetricGate_ConservationHolds_ProbeLive_Yields_Pass()
    {
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = ConservingSnapshot(results: 9);
        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");
        Assert.True(report.MetricGate.ConservationOk);
        Assert.True(report.MetricGate.ProbeLiveOk);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void MetricGate_ConservationBroken_Yields_Fail()
    {
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = ConservingSnapshot(results: 9) with { OrchestratorMessagesConsumedAtEnd = 9, ProcessorMessagesSentAtEnd = 4 };
        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");
        Assert.False(report.MetricGate.ConservationOk);
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEmpty(report.CorroborationDetail);
    }

    [Fact]
    public void MetricGate_ConservationOff_NotBinding_Yields_Pass()
    {
        // A scenario whose conservation counter reset (or L2 was wiped) shows ConservationOk=false, but
        // because MG-1 is reporting-only for it (mg1Binding:false), the verdict is NOT gated on it.
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = ConservingSnapshot(results: 9) with { OrchestratorMessagesConsumedAtEnd = 9, ProcessorMessagesSentAtEnd = 4 };
        var report = new PassFailEngine().Analyze(new[] { run }, snap, "TEST-02", mg1Binding: false);
        Assert.False(report.MetricGate.ConservationOk);   // still reported
        Assert.False(report.MetricGate.Mg1Binding);       // but not binding
        Assert.Empty(report.CorroborationDetail);         // no FAIL line emitted (silent)
        Assert.Equal(Verdict.Pass, report.Verdict);       // verdict not gated on MG-1 here
    }

    [Fact]
    public void MetricGate_ProbeDead_Yields_Fail()
    {
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = ConservingSnapshot(results: 9, probeRate: 0.0);
        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");
        Assert.False(report.MetricGate.ProbeLiveOk);
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public void MetricGate_KeeperExpected_ButSilent_Yields_Fail()
    {
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = ConservingSnapshot(results: 9, keeperConsumed: 0, keeperSent: 0);
        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test", expectsKeeperActivity: true);
        Assert.False(report.MetricGate.KeeperRecoveryOk);
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public void MetricGate_KeeperExpected_AndActive_Yields_Pass()
    {
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = ConservingSnapshot(results: 9, keeperConsumed: 5, keeperSent: 4);
        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test", expectsKeeperActivity: true);
        Assert.True(report.MetricGate.KeeperRecoveryOk);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    // B-criterion: a started-but-stalled run (4 hops only, missing the rest) — used to drive the in-flight
    // vs post-recovery classification facts below.
    private static readonly string[] Missing4Hops = { "Step_A", "Step_B", "Step_C", "Step_D1" }; // started, stalled

    [Fact]
    public void InFlight_LossBeforeRecovery_IsTolerated_Yields_Pass()
    {
        var complete = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var stalled  = RunTrace.FromLabels("corr-2", "exec-2", Missing4Hops);
        var recovery = DateTimeOffset.Parse("2026-06-18T10:00:30Z");
        var lastHop  = new Dictionary<string, DateTimeOffset> { ["corr-2|exec-2"] = DateTimeOffset.Parse("2026-06-18T10:00:10Z") };
        var firstHop = new Dictionary<string, DateTimeOffset> { ["corr-2|exec-2"] = DateTimeOffset.Parse("2026-06-18T10:00:05Z") };
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(new[] { complete, stalled }, snap, "TEST-05",
            recoveryUtc: recovery, firstHopUtcByExecution: firstHop, lastHopUtcByExecution: lastHop);

        Assert.Equal(1, report.InFlightLoss);
        Assert.Equal(0, report.Missing);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void Incomplete_StartedAfterRecovery_Yields_Fail()
    {
        var stalled  = RunTrace.FromLabels("corr-9", "exec-9", Missing4Hops);
        var recovery = DateTimeOffset.Parse("2026-06-18T10:00:30Z");
        var lastHop  = new Dictionary<string, DateTimeOffset> { ["corr-9|exec-9"] = DateTimeOffset.Parse("2026-06-18T10:01:10Z") };
        var firstHop = new Dictionary<string, DateTimeOffset> { ["corr-9|exec-9"] = DateTimeOffset.Parse("2026-06-18T10:01:05Z") };
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(new[] { stalled }, snap, "TEST-05",
            recoveryUtc: recovery, firstHopUtcByExecution: firstHop, lastHopUtcByExecution: lastHop);

        Assert.Equal(1, report.Missing);
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public void InFlightLoss_ManyCleanDrops_DoNotFail_Yields_Pass()
    {
        // D75-2: the absolute MaxInFlightLoss bound is GONE. Six started-but-incomplete runs, ALL
        // keeper-confirmed clean-absent DROPs (provably-unrecoverable) → tolerated regardless of count.
        // Under the OLD engine six in-flight losses (> 4) forced a FAIL on the cron-rate-coupled bound;
        // now N clean drops (any N) do NOT by themselves fail — the verdict is a pure function of
        // per-(corr,exec) recoverability, never a firing-rate proxy.
        var runs = Enumerable.Range(1, 6)
            .Select(i => RunTrace.FromLabels($"corr-{i}", $"exec-{i}", Missing4Hops)).ToArray();
        var keeperDrops = runs.ToDictionary(
            r => $"{r.CorrelationId}|{r.ExecutionId}", _ => "drop", StringComparer.Ordinal);
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(runs, snap, "TEST-06",
            keeperOutcomeByExecution: keeperDrops);

        Assert.Equal(0, report.Missing);
        Assert.Equal(Verdict.Pass, report.Verdict);   // any count of clean drops passes — no absolute bound
    }

    [Fact]
    public void KeeperDrop_MarksIncomplete_Tolerated_Yields_Pass()
    {
        // D75-3 (tolerated): one COMPLETE run + one incomplete run whose (corr,exec) is keeper-confirmed
        // a clean-absent DROP (provably-unrecoverable). Tolerance is driven by keeper EVIDENCE, not the
        // timestamp heuristic (no recoveryUtc/firstHop/lastHop supplied). → InFlightLoss==1, Missing==0, Pass.
        var complete = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var stalled  = RunTrace.FromLabels("corr-2", "exec-2", Missing4Hops);
        var keeperOutcome = new Dictionary<string, string>(StringComparer.Ordinal) { ["corr-2|exec-2"] = "drop" };
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(new[] { complete, stalled }, snap, "TEST-06",
            keeperOutcomeByExecution: keeperOutcome);

        Assert.Equal(1, report.InFlightLoss);
        Assert.Equal(0, report.Missing);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail()
    {
        // D75-3 (binding): one incomplete run with NO keeper outcome entry and last-hop AFTER recovery
        // (not stalled-before-recovery). It was recoverable (keeper could have reinjected) but did not
        // complete → binding FAIL. Missing==1, Fail.
        var stalled  = RunTrace.FromLabels("corr-9", "exec-9", Missing4Hops);
        var recovery = DateTimeOffset.Parse("2026-06-18T10:00:30Z");
        var lastHop  = new Dictionary<string, DateTimeOffset> { ["corr-9|exec-9"] = DateTimeOffset.Parse("2026-06-18T10:01:10Z") };
        var firstHop = new Dictionary<string, DateTimeOffset> { ["corr-9|exec-9"] = DateTimeOffset.Parse("2026-06-18T10:01:05Z") };
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(new[] { stalled }, snap, "TEST-04",
            recoveryUtc: recovery, firstHopUtcByExecution: firstHop, lastHopUtcByExecution: lastHop,
            keeperOutcomeByExecution: new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(1, report.Missing);
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public void RedisWipe_StalledBeforeRecovery_NoKeeperDrop_Yields_Pass()
    {
        // D75-5 (redis-wipe timestamp path preserved): one incomplete run with NO keeper outcome entry but
        // last-hop BEFORE recovery (stalled-before-recovery, in-flight-at-wipe). The timestamp heuristic still
        // tolerates redis-crash TEST-05/07 which produce NO keeper drop log (a Redis exception routes to
        // exhaustion, not a clean-absent drop). → InFlightLoss==1, Missing==0, Pass.
        var complete = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var stalled  = RunTrace.FromLabels("corr-2", "exec-2", Missing4Hops);
        var recovery = DateTimeOffset.Parse("2026-06-18T10:00:30Z");
        var lastHop  = new Dictionary<string, DateTimeOffset> { ["corr-2|exec-2"] = DateTimeOffset.Parse("2026-06-18T10:00:10Z") };
        var firstHop = new Dictionary<string, DateTimeOffset> { ["corr-2|exec-2"] = DateTimeOffset.Parse("2026-06-18T10:00:05Z") };
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(new[] { complete, stalled }, snap, "TEST-05",
            recoveryUtc: recovery, firstHopUtcByExecution: firstHop, lastHopUtcByExecution: lastHop,
            keeperOutcomeByExecution: new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(1, report.InFlightLoss);
        Assert.Equal(0, report.Missing);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void KeeperReinject_VetoesRedisWipeTolerance_StalledBeforeRecovery_Yields_Fail()
    {
        // WR-01 (D75-5 veto): an incomplete run whose keeper outcome is "reinject" — HARD evidence the L2 data
        // WAS recoverable and the keeper confirmed a re-send — MUST be a BINDING FAIL even when its timestamps
        // satisfy the redis-wipe tolerance path (last hop < recovery, no first-hop-after-recovery). Without the
        // veto this run would be falsely tolerated via redisWipeInFlight; with it, the reinject evidence wins and
        // the recoverable-but-still-incomplete execution is a binding miss (Missing==1, Fail).
        var stalled  = RunTrace.FromLabels("corr-2", "exec-2", Missing4Hops);
        var recovery = DateTimeOffset.Parse("2026-06-18T10:00:30Z");
        // Timestamps satisfy stalled-before-recovery (last hop < recovery) and NOT started-after-recovery —
        // exactly the RedisWipe_StalledBeforeRecovery tolerance window, so the ONLY differentiator is the
        // "reinject" keeper outcome vetoing it.
        var lastHop  = new Dictionary<string, DateTimeOffset> { ["corr-2|exec-2"] = DateTimeOffset.Parse("2026-06-18T10:00:10Z") };
        var firstHop = new Dictionary<string, DateTimeOffset> { ["corr-2|exec-2"] = DateTimeOffset.Parse("2026-06-18T10:00:05Z") };
        var keeperOutcome = new Dictionary<string, string>(StringComparer.Ordinal) { ["corr-2|exec-2"] = "reinject" };
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(new[] { stalled }, snap, "TEST-05",
            recoveryUtc: recovery, firstHopUtcByExecution: firstHop, lastHopUtcByExecution: lastHop,
            keeperOutcomeByExecution: keeperOutcome);

        Assert.Equal(0, report.InFlightLoss);   // NOT tolerated — the reinject veto overrode the timestamp path
        Assert.Equal(1, report.Missing);        // recoverable-but-lost → binding miss
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEmpty(report.MissingDetail);
    }

    // ── Phase 76 (ANL-01/02/03, SMP-01): the STEPID-KEYED structural path — synthetic stepId sets, ZERO
    //    Step_* labels. These prove the re-keyed completeness / ES-derived expected set / framework-redundancy
    //    reconciliation directly, independent of the value-oracle label facts above. ────────────────────────

    // The 9 per-execution HOP stepIds (the convergent terminal `hop-g` fans in ×2 as RAW stepIds → DISTINCT 9).
    // Deliberately NOT `Step_*` — the structural path is label-free (D-15).
    private static readonly string[] FullHopStepIds =
        { "hop-b", "hop-c", "hop-d1", "hop-e1", "hop-f1", "hop-d2", "hop-e2", "hop-f2", "hop-g", "hop-g" };
    private const string ConvergentStepId = "hop-g";

    /// <summary>The DISTINCT 9-hop stepId expected set (the convergent terminal collapsed to one).</summary>
    private static IReadOnlySet<string> DistinctHopStepIds() =>
        new HashSet<string>(FullHopStepIds, StringComparer.Ordinal);

    /// <summary>Build a per-(corr,exec) ES-derived expected-set map keyed "corr|exec".</summary>
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Expect(
        params (string key, IReadOnlySet<string> steps)[] entries) =>
        entries.ToDictionary(e => e.key, e => e.steps, StringComparer.Ordinal);

    [Fact]
    public void PassFailEngine_StepIdCompleteness_AllRunsCover_Yields_Pass()
    {
        // ANL-01: completeness is stepId-keyed (DistinctStepIds) against the ES-derived expected set — NO
        // Step_* label present anywhere. Two runs, each covering the 9-hop expected set (hop-g ×2 legitimate
        // fan-in) → complete → Pass.
        var runs = new[]
        {
            RunTrace.FromStepIds("corr-1", "exec-1", FullHopStepIds, convergentStepId: ConvergentStepId),
            RunTrace.FromStepIds("corr-2", "exec-2", FullHopStepIds, convergentStepId: ConvergentStepId),
        };
        var expected = Expect(
            ("corr-1|exec-1", DistinctHopStepIds()),
            ("corr-2|exec-2", DistinctHopStepIds()));

        var report = new PassFailEngine().Analyze(runs, CleanSnapshot(), "unit-stepid",
            expectedStepIdsByExecution: expected);

        Assert.Equal(2, report.StartedRuns);
        Assert.Equal(2, report.CompleteRuns);
        Assert.Equal(0, report.Missing);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void PassFailEngine_ExpectedSet_Test08_DispatchedButNeverExecuted_Yields_Fail()
    {
        // ANL-02 (closes TEST-08): a stepId present in the ES-derived expected set (the orchestrator FW-02
        // dispatched it) but ABSENT from the observed processor records is a binding miss — dispatched-but-
        // never-executed. Observed drops hop-f2; expected still carries all 9 → Missing 1 → Fail.
        var observed = FullHopStepIds.Where(s => s != "hop-f2").ToArray();
        var run = RunTrace.FromStepIds("corr-1", "exec-1", observed, convergentStepId: ConvergentStepId);
        var expected = Expect(("corr-1|exec-1", DistinctHopStepIds()));

        var report = new PassFailEngine().Analyze(new[] { run }, CleanSnapshot(), "unit-test08",
            expectedStepIdsByExecution: expected);

        Assert.Equal(1, report.StartedRuns);
        Assert.Equal(0, report.CompleteRuns);
        Assert.Equal(1, report.Missing);                 // dispatched-but-never-executed → binding miss
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEmpty(report.MissingDetail);
    }

    [Fact]
    public void PassFailEngine_EntryMarker_EmptyExecutionId_Excluded_From_Scoring_D09()
    {
        // D-09: a framework record with ExecutionId == Guid.Empty is an ENTRY MARKER — counted as entry-ran,
        // EXCLUDED from every (corr,exec) expected/complete set. Here it would be "incomplete" (only hop-b) but
        // must NOT be scored as a started run or a miss; the one real complete run alone drives the verdict.
        var marker = RunTrace.FromStepIds("corr-1", Guid.Empty.ToString(), new[] { "hop-b" },
            convergentStepId: ConvergentStepId);
        var real = RunTrace.FromStepIds("corr-1", "exec-1", FullHopStepIds, convergentStepId: ConvergentStepId);
        var expected = Expect(("corr-1|exec-1", DistinctHopStepIds()));

        var report = new PassFailEngine().Analyze(new[] { marker, real }, CleanSnapshot(), "unit-d09",
            expectedStepIdsByExecution: expected);

        Assert.Equal(1, report.StartedRuns);             // the marker is NOT a started run
        Assert.Equal(1, report.CompleteRuns);
        Assert.Equal(0, report.Missing);                 // the marker is NOT a miss
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void PassFailEngine_OracleAbsent_FrameworkRecordsOnly_ValueChainNotApplicable_Yields_Pass()
    {
        // SMP-01: with framework StepId records ONLY (no StepLabel/Received/Produced, no seed oracle), the
        // stepId completeness/expected-set still computes correctly, and the value chain degrades to
        // NOT-APPLICABLE (ValueChainOk stays true) — never FAIL. A complete cohort with zero author values
        // yields Pass.
        var run = RunTrace.FromStepIds("corr-1", "exec-1", FullHopStepIds, convergentStepId: ConvergentStepId);
        var expected = Expect(("corr-1|exec-1", DistinctHopStepIds()));

        var report = new PassFailEngine().Analyze(new[] { run }, CleanSnapshot(), "unit-oracle-absent",
            expectedStepIdsByExecution: expected);   // seedsByExecution NULL ⇒ value chain N/A

        Assert.Equal(1, report.CompleteRuns);
        Assert.True(report.ValueChainOk);                // N/A treated as OK, NOT a vacuous-green FAIL
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void PassFailEngine_ReconcileRedundancy_MissingHop_OrchestratorConsumed_NoSeedOracle_Yields_Pass()
    {
        // ANL-03: a run missing hop-f2's OWN processor record but whose missing hop is PROVEN-run by an
        // orchestrator FW-02 record that consumed its output EntryId (M_N) reconciles as a NON-binding telemetry
        // gap — WITH NO seed oracle (seedsByExecution NULL, the ANL-03 acceptance). Missing 0, TelemetryGap 1,
        // Pass. (Contrast the value-oracle telemetry-gap path, which REQUIRES a seed + terminal-anchor.)
        var observed = FullHopStepIds.Where(s => s != "hop-f2").ToArray();
        var run = RunTrace.FromStepIds("corr-1", "exec-1", observed, convergentStepId: ConvergentStepId);
        var expected = Expect(("corr-1|exec-1", DistinctHopStepIds()));
        var proven = Expect(("corr-1|exec-1",
            (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "hop-f2" }));

        var report = new PassFailEngine().Analyze(new[] { run }, CleanSnapshot(), "unit-redundancy",
            expectedStepIdsByExecution: expected,
            orchestratorConsumedStepIdsByExecution: proven);   // seedsByExecution NULL — no value oracle

        Assert.Equal(0, report.Missing);                       // reconciled, not a recoverable-but-lost miss
        Assert.Equal(1, report.TelemetryGap);                  // framework-redundancy telemetry gap (non-binding)
        Assert.Equal(Verdict.Pass, report.Verdict);
        Assert.NotEmpty(report.TelemetryGapDetail);
    }

    [Fact]
    public void PassFailEngine_ReconcileRedundancy_MissingBoth_Yields_Fail()
    {
        // ANL-03 guard: a run missing hop-f2's processor record AND with NO orchestrator-consumed evidence for
        // it (missing BOTH records) stays a binding miss. Missing 1, Fail.
        var observed = FullHopStepIds.Where(s => s != "hop-f2").ToArray();
        var run = RunTrace.FromStepIds("corr-1", "exec-1", observed, convergentStepId: ConvergentStepId);
        var expected = Expect(("corr-1|exec-1", DistinctHopStepIds()));

        var report = new PassFailEngine().Analyze(new[] { run }, CleanSnapshot(), "unit-redundancy-miss",
            expectedStepIdsByExecution: expected,
            orchestratorConsumedStepIdsByExecution: Expect());   // empty proven set — no redundancy evidence

        Assert.Equal(1, report.Missing);
        Assert.Equal(0, report.TelemetryGap);
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEmpty(report.MissingDetail);
    }

    // ── Phase 76 (ANL-04/05, D-01): the THREE-CLASS verdict — the verdict splits on EVIDENCE SUFFICIENCY,
    //    not severity. FAIL requires POSITIVE evidence of loss; total trace darkness (startedRuns==0) with
    //    self-consistent conservation is INCONCLUSIVE (collector-blind / TEST-01 cold-ES), never a false FAIL
    //    and never a vacuous green; a GENUINE conservation gap stays FAIL. ──────────────────────────────────

    // A trace-dark window (ZERO started runs) whose conservation is self-consistent at a LIVE (non-zero,
    // EQUAL) counter pair — the TEST-01 cold-ES shape where ES is cold but the stack itself is healthy.
    private static PromCounterSnapshot ConservationIntactTraceDarkSnapshot(double probeRate = 0.2) => new()
    {
        OrchestratorMessagesSentDelta = 0,
        OrchestratorMessagesConsumedDelta = 0,
        ProcessorMessagesConsumedDelta = 0,
        ProcessorMessagesSentDelta = 0,
        OrchestratorMessagesConsumedAtEnd = 50,   // == processor_sent@end → conservation intact (gap 0)
        ProcessorMessagesSentAtEnd = 50,
        KeeperMessagesConsumedDelta = 0,
        KeeperMessagesSentDelta = 0,
        KeeperL2ProbeRate = probeRate,
    };

    // A fully COLLECTOR-BLIND window: every counter frozen/absent at zero AND the probe dead — Prometheus
    // scrapes an absent collector, so conservation reads self-consistent (0 == 0) but the gate fails on MG-3.
    private static PromCounterSnapshot CollectorBlindSnapshot() => new()
    {
        OrchestratorMessagesSentDelta = 0,
        OrchestratorMessagesConsumedDelta = 0,
        ProcessorMessagesConsumedDelta = 0,
        ProcessorMessagesSentDelta = 0,
        OrchestratorMessagesConsumedAtEnd = 0,   // frozen/absent — conservation vacuously self-consistent
        ProcessorMessagesSentAtEnd = 0,
        KeeperMessagesConsumedDelta = 0,
        KeeperMessagesSentDelta = 0,
        KeeperL2ProbeRate = 0.0,                 // probe DEAD → metric gate fails on MG-3 (collector blind)
    };

    [Fact]
    public void Verdict_TraceDark_ConservationIntact_MetricGateFalse_Yields_Inconclusive()
    {
        // ANL-04a: total trace darkness (startedRuns == 0) + self-consistent conservation (orch_consumed ==
        // proc_sent) → INCONCLUSIVE REGARDLESS of metricGateOk. Here the probe is DEAD so the metric gate is
        // FALSE — the OLD two-class engine would have flipped this to FAIL (a false data-loss report), but the
        // gate failure is the collector being blind, not a conservation gap, so the run is Inconclusive.
        var snap = ConservationIntactTraceDarkSnapshot(probeRate: 0.0);   // metricGateOk false via dead probe

        var report = new PassFailEngine().Analyze(Array.Empty<RunTrace>(), snap, "TEST-01");

        Assert.Equal(0, report.StartedRuns);                 // trace-dark
        Assert.True(report.MetricGate.ConservationOk);       // conservation self-consistent (50 == 50)
        Assert.False(report.MetricGate.ProbeLiveOk);         // metric gate is FALSE here
        Assert.Equal(Verdict.Inconclusive, report.Verdict);  // …yet the verdict is Inconclusive, not Fail
        Assert.NotEmpty(report.CorroborationDetail);         // blind-case reason carried in the report
    }

    [Fact]
    public void Verdict_TraceDark_ConservationIntact_MetricGateHolds_Yields_Inconclusive_NotVacuousPass()
    {
        // ANL-04a (the core TEST-01 fix): trace darkness + conservation intact + a CLEAN metric gate (probe
        // LIVE). Under the OLD two-class engine this scored missing==0/no-dup/valueChainOk/metricGateOk → a
        // VACUOUS GREEN. The three-class gate returns INCONCLUSIVE: zero started runs is not evidence of
        // success, it is absence of evidence.
        var snap = ConservationIntactTraceDarkSnapshot(probeRate: 0.2);   // metricGateOk TRUE (probe live)

        var report = new PassFailEngine().Analyze(Array.Empty<RunTrace>(), snap, "TEST-01");

        Assert.Equal(0, report.StartedRuns);
        Assert.True(report.MetricGate.ConservationOk);
        Assert.True(report.MetricGate.ProbeLiveOk);          // metric gate HOLDS — would have been vacuous PASS
        Assert.Equal(Verdict.Inconclusive, report.Verdict);  // …but zero evidence → Inconclusive, not Pass
    }

    [Fact]
    public void Verdict_PartialEvidence_RealConservationGap_Yields_Fail()
    {
        // ANL-04b: PARTIAL evidence (a started-but-incomplete run, startedRuns > 0) + a REAL conservation gap
        // (orch_consumed != proc_sent). Evidence is SUFFICIENT (a run started), so the gate does not short to
        // Inconclusive; the missing hop + the genuine gap are positive evidence of loss → FAIL.
        var incomplete = RunTrace.FromLabels("corr-1", "exec-1", Missing4Hops);
        var snap = CleanSnapshot() with { OrchestratorMessagesConsumedAtEnd = 9, ProcessorMessagesSentAtEnd = 4 };

        var report = new PassFailEngine().Analyze(new[] { incomplete }, snap, "unit-anl04b");

        Assert.Equal(1, report.StartedRuns);                 // partial evidence present (not trace-dark)
        Assert.False(report.MetricGate.ConservationOk);      // genuine conservation gap
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEqual(Verdict.Inconclusive, report.Verdict);
    }

    [Fact]
    public void Verdict_CompleteCohort_Yields_Pass()
    {
        // ANL-04c: a COMPLETE framework-record cohort with a clean metric gate → PASS (evidence sufficient
        // and positive).
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);

        var report = new PassFailEngine().Analyze(new[] { run }, CleanSnapshot(), "unit-anl04c");

        Assert.Equal(1, report.CompleteRuns);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void Verdict_NonZeroTelemetryGap_StaysPass_D01()
    {
        // D-01: a reconciled run with a NON-ZERO TelemetryGap stays PASS (preserves 6e90216). A run missing
        // hop-f2's own processor record but whose missing hop is proven-run by an orchestrator FW-02 record
        // (framework redundancy, NO seed oracle) is a non-binding telemetry gap — Missing 0, TelemetryGap 1.
        // The three-class gate must NOT let the non-zero gap flip PASS → FAIL or → Inconclusive.
        var observed = FullHopStepIds.Where(s => s != "hop-f2").ToArray();
        var run = RunTrace.FromStepIds("corr-1", "exec-1", observed, convergentStepId: ConvergentStepId);
        var expected = Expect(("corr-1|exec-1", DistinctHopStepIds()));
        var proven = Expect(("corr-1|exec-1",
            (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "hop-f2" }));

        var report = new PassFailEngine().Analyze(new[] { run }, CleanSnapshot(), "unit-d01",
            expectedStepIdsByExecution: expected,
            orchestratorConsumedStepIdsByExecution: proven);

        Assert.Equal(1, report.TelemetryGap);                // non-zero telemetry gap
        Assert.Equal(0, report.Missing);
        Assert.Equal(Verdict.Pass, report.Verdict);          // D-01: non-binding gap stays PASS
    }

    [Fact]
    public void MetricGate_CollectorBlind_FrozenCounters_TraceDark_Yields_Inconclusive()
    {
        // ANL-05 (inverted metric gate — blind case): frozen/absent counters + absent traces. The gate FAILS
        // (probe dead, MG-3) but the cause is the observability tier being blind (Prometheus scrapes an absent
        // collector), NOT a genuine conservation gap → INCONCLUSIVE, not FAIL. This is the inversion: a metric
        // gate failing because the metrics tier is absent must never be reported as data loss.
        var report = new PassFailEngine().Analyze(Array.Empty<RunTrace>(), CollectorBlindSnapshot(), "TEST-01");

        Assert.Equal(0, report.StartedRuns);
        Assert.True(report.MetricGate.ConservationOk);       // 0 == 0, vacuously self-consistent
        Assert.False(report.MetricGate.ProbeLiveOk);         // gate FALSE (collector blind)
        Assert.Equal(Verdict.Inconclusive, report.Verdict);  // inverted → Inconclusive, not Fail
    }

    [Fact]
    public void MetricGate_LiveCounters_GenuineGap_NotInconclusive_Yields_Fail()
    {
        // ANL-05 (live case — the T-76-12 guard): a run with LIVE counters and a GENUINE orch_consumed !=
        // proc_sent gap is positive evidence of loss → FAIL, NOT Inconclusive. INCONCLUSIVE must never be
        // misused to mask a real conservation gap — it fires only on frozen/absent counters + absent traces.
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = ConservingSnapshot(results: 9) with { OrchestratorMessagesConsumedAtEnd = 9, ProcessorMessagesSentAtEnd = 4 };

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-anl05-live");

        Assert.False(report.MetricGate.ConservationOk);      // genuine live gap (9 != 4)
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEqual(Verdict.Inconclusive, report.Verdict);
    }

    [Fact]
    public void RemovedDedupCounters_HaveNoSnapshotField_AbsenceProvenByCompile()
    {
        // D-14 absence proof (compile-time): the three removed counters
        //   orchestrator_result_deduped / processor_dispatch_deduped / keeper_reinject_dropped
        // and the processor `outcome` breakdown no longer have ANY PromCounterSnapshot field. A clean
        // corroborating snapshot constructs with EXACTLY the four uniform deltas and gates PASS — there is
        // no dedup/drop/outcome series to set, so their absence is structurally guaranteed by this file
        // compiling. (The live no-series assertion over Prometheus is owned by the RealStack analyzer.)
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");

        Assert.Equal(Verdict.Pass, report.Verdict);
    }
}
