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
/// <item><c>PromDeadRun_ImpliesMoreRunsThanEs_Yields_NonFatalWarning</c> — 67-03: Prom excess ⇒ WARNING, NOT Fail.</item>
/// <item><c>PromWindowEdge_OneRunMismatch_WithinTolerance_StaysClean</c> — 67-03: ±1-run boundary tolerance, no warning, Pass.</item>
/// <item><c>RetiredConflation_ProcessorSentShort_DoesNotFailVerdict</c> — 67-03: ResultSentCompleted short no longer fails (retired #2).</item>
/// <item><c>RemovedDedupCounters_HaveNoSnapshotField_AbsenceProvenByCompile</c> — D-14: the 3 removed dedup/drop counters have no snapshot field (absence by compile).</item>
/// <item><c>SpawnAware_ResultExceedsDispatchByExactlySpawnExtra_StaysClean</c> — spawn-aware OBS-03: the entry fan-out's extra result reconciles CLEAN.</item>
/// <item><c>SpawnAware_ResultMismatch_RaisesNonFatalWarning</c> — spawn-aware OBS-03: a wrong result/dispatch gap is a non-fatal WARNING.</item>
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

    /// <summary>
    /// A Prom snapshot CORROBORATING <paramref name="startedRuns"/> ES-observed runs: the orchestrator
    /// dispatches once per step, so OrchestratorMessagesSentDelta = startedRuns × 9 (⇒ impliedRuns =
    /// startedRuns). The three legacy dedup/drop counters are removed entirely (Phase 74, D-13/D-14) and
    /// the processor `outcome` breakdown is gone. Corroboration is non-binding (67-03) — this shape simply
    /// keeps the corroboration cross-check clean so a fact isolates ONE branch.
    /// </summary>
    private static PromCounterSnapshot CorroboratingSnapshot(int startedRuns) => new()
    {
        OrchestratorMessagesSentDelta = startedRuns * PassFailEngine.LabelsPerRun,
        OrchestratorMessagesConsumedDelta = startedRuns * PassFailEngine.LabelsPerRun,
        ProcessorMessagesConsumedDelta = startedRuns * PassFailEngine.LabelsPerRun,
        ProcessorMessagesSentDelta = startedRuns * PassFailEngine.LabelsPerRun,
    };

    /// <summary>triggerCount mirrors the fixture via the shared <see cref="PassFailEngine.TriggerCountFrom"/> (IN-01) — corroboration evidence only (67-03).</summary>
    private static int TriggerCountOf(PromCounterSnapshot s) => PassFailEngine.TriggerCountFrom(s);

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
        var snap = CorroboratingSnapshot(startedRuns: 3);

        var report = new PassFailEngine().Analyze(runs, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(3, report.StartedRuns);                                   // ES-binding denominator
        Assert.Equal(3, report.CompleteRuns);                                  // OBS-01
        Assert.Equal(0, report.Missing);
        Assert.Equal(ReconciliationOutcome.Reconciled, report.Reconciliation); // Prom corroboration clean
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void Incomplete_StartedRun_DropsStepF2_Yields_Fail()
    {
        // The run STARTED (it logged Step_A…) but is missing the Step_F2 sink → 9 distinct labels → incomplete.
        // Step_G ×2 stays legitimate (not a duplicate); the ONLY failure driver is the missing Step_F2.
        var missingF2Labels = AllTenLabelsWithConvergentGx2.Where(l => l != "Step_F2").ToArray();
        var run = RunTrace.FromLabels("corr-1", "exec-1", missingF2Labels);
        var snap = CorroboratingSnapshot(startedRuns: 1);

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

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
        var snap = CorroboratingSnapshot(startedRuns: 1);

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(Verdict.Fail, report.Verdict);   // BINDING fail-closed even with every distinct label present
        Assert.NotEmpty(report.Duplicates);
    }

    [Fact]
    public void PromDeadRun_ImpliesMoreRunsThanEs_Yields_NonFatalWarning()
    {
        // ES observed 1 started+complete run, but Prom dispatched ~3 runs' worth of steps (27 / 9 = 3):
        // 2 fully-dead runs (dispatched, Step_A never logged). Under the OLD binding model this was a
        // hard Fail; under 67-03 it is a NON-FATAL corroboration warning — the ES-binding verdict (every
        // started run complete, no duplicate) still PASSES.
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = CorroboratingSnapshot(startedRuns: 1) with
        {
            OrchestratorMessagesSentDelta = 3 * PassFailEngine.LabelsPerRun, // implies 3 runs vs ES 1
            OrchestratorMessagesConsumedDelta = 3 * PassFailEngine.LabelsPerRun, // keep spawn-aware recon clean (excess isolates the dead-run branch)
        };

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(1, report.StartedRuns);
        Assert.Equal(3, report.PromImpliedRuns);                                 // dispatch-implied run count
        Assert.Equal(ReconciliationOutcome.Unreconciled, report.Reconciliation); // corroboration WARNING raised
        Assert.NotEmpty(report.CorroborationDetail);
        Assert.Equal(Verdict.Pass, report.Verdict);                              // NON-FATAL — ES-binding still passes
    }

    [Fact]
    public void PromWindowEdge_OneRunMismatch_WithinTolerance_StaysClean()
    {
        // The documented ~1-run window-edge mismatch (e.g. Prom 81 = 9×9 ⇒ implied 9 vs ES 10, or the
        // mirror implied 11 vs ES 10). A ±1-run tolerance keeps corroboration clean and the verdict green
        // for a ±1 mismatch in EITHER direction. NOTE (WR-01): the warning that fires OUTSIDE tolerance is
        // one-directional by design — only a POSITIVE excess (implied > started, the dead-run signal) is
        // flagged; an out-of-tolerance NEGATIVE excess (started > implied) is intentionally not a warning
        // (see the asymmetry note in PassFailEngine.cs). Within ±1, both directions stay clean regardless.
        var runs = Enumerable.Range(1, 10)
            .Select(i => RunTrace.FromLabels($"corr-{i}", $"exec-{i}", AllTenLabelsWithConvergentGx2))
            .ToArray();
        var snap = CorroboratingSnapshot(startedRuns: 10) with
        {
            OrchestratorMessagesSentDelta = 11 * PassFailEngine.LabelsPerRun, // implies 11 vs ES 10 → excess 1 == tolerance
            OrchestratorMessagesConsumedDelta = 11 * PassFailEngine.LabelsPerRun, // keep spawn-aware recon clean (excess isolates the window-edge branch)
        };

        var report = new PassFailEngine().Analyze(runs, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(10, report.StartedRuns);
        Assert.Equal(11, report.PromImpliedRuns);
        Assert.Equal(ReconciliationOutcome.Reconciled, report.Reconciliation); // within ±1 — no warning
        Assert.Empty(report.CorroborationDetail);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void RetiredConflation_ProcessorSentShort_DoesNotFailVerdict()
    {
        // Retired conflation #2: under the OLD model the processor sent-completed counter < complete × 9
        // forced an Unreconciled FAIL. Under 67-03 that arithmetic is gone from the binding gate — a complete
        // ES-binding cohort PASSES regardless of the processor_messages_sent counter value.
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = CorroboratingSnapshot(startedRuns: 1) with
        {
            ProcessorMessagesSentDelta = 8, // short of 9 — would have failed the OLD binding gate
        };

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(1, report.CompleteRuns);
        Assert.Equal(Verdict.Pass, report.Verdict); // ES-binding pass; the retired counter no longer gates
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
        var snap = CorroboratingSnapshot(startedRuns: 1);

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void SpawnAware_ResultExceedsDispatchByExactlySpawnExtra_StaysClean()
    {
        // Two cron fires (2 distinct correlationIds), each spawning 2 execution instances → 4 runs. The entry
        // step emits 2 results from 1 dispatch, so OrchestratorMessagesConsumedDelta runs ahead of OrchestratorMessagesSentDelta by
        // exactly spawnExtra = the number of entry dispatches = distinct correlationIds = 2. Spawn-aware OBS-03
        // reconciles CLEAN (no warning); the ES-binding verdict (every run complete, no duplicate) PASSES.
        var runs = new[]
        {
            RunTrace.FromLabels("corr-1", "exec-1a", AllTenLabelsWithConvergentGx2),
            RunTrace.FromLabels("corr-1", "exec-1b", AllTenLabelsWithConvergentGx2),
            RunTrace.FromLabels("corr-2", "exec-2a", AllTenLabelsWithConvergentGx2),
            RunTrace.FromLabels("corr-2", "exec-2b", AllTenLabelsWithConvergentGx2),
        };
        var spawnExtra = runs.Select(r => r.CorrelationId).Distinct().Count();   // derived from data = 2
        var dispatch = 4 * PassFailEngine.LabelsPerRun;                          // 4 instances' worth of step dispatches
        var snap = CorroboratingSnapshot(startedRuns: 4) with
        {
            OrchestratorMessagesSentDelta = dispatch,
            OrchestratorMessagesConsumedDelta = dispatch + spawnExtra,           // entry fan-out's extra results
        };

        var report = new PassFailEngine().Analyze(runs, snap, TriggerCountOf(snap), "unit-test", spawnExtra);

        Assert.Equal(4, report.StartedRuns);
        Assert.Equal(2, report.SpawnExtra);
        Assert.Equal(dispatch + spawnExtra, report.ExpectedResultConsumed);
        Assert.Equal(ReconciliationOutcome.Reconciled, report.Reconciliation);   // spawn-aware recon clean
        Assert.Empty(report.CorroborationDetail);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void SpawnAware_ResultMismatch_RaisesNonFatalWarning()
    {
        // Same 4-instance cohort, but the result counter is OFF by far more than the spawnExtra + slack (a
        // result/dispatch imbalance). Spawn-aware OBS-03 raises a NON-FATAL warning; the ES-binding verdict
        // (every run complete, no duplicate) still PASSES — Prom is corroborating only.
        var runs = new[]
        {
            RunTrace.FromLabels("corr-1", "exec-1a", AllTenLabelsWithConvergentGx2),
            RunTrace.FromLabels("corr-1", "exec-1b", AllTenLabelsWithConvergentGx2),
            RunTrace.FromLabels("corr-2", "exec-2a", AllTenLabelsWithConvergentGx2),
            RunTrace.FromLabels("corr-2", "exec-2b", AllTenLabelsWithConvergentGx2),
        };
        var spawnExtra = runs.Select(r => r.CorrelationId).Distinct().Count();   // 2
        var dispatch = 4 * PassFailEngine.LabelsPerRun;
        var snap = CorroboratingSnapshot(startedRuns: 4) with
        {
            OrchestratorMessagesSentDelta = dispatch,
            // Expected = dispatch + 2; supply dispatch + 2 + (3 runs' worth) far beyond the ±1-run slack.
            OrchestratorMessagesConsumedDelta = dispatch + spawnExtra + (3 * PassFailEngine.LabelsPerRun),
        };

        var report = new PassFailEngine().Analyze(runs, snap, TriggerCountOf(snap), "unit-test", spawnExtra);

        Assert.Equal(ReconciliationOutcome.Unreconciled, report.Reconciliation); // spawn-aware warning raised
        Assert.NotEmpty(report.CorroborationDetail);
        Assert.Equal(Verdict.Pass, report.Verdict);                              // NON-FATAL — ES-binding still passes
    }
}
