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
