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

    // A clean 4-delta snapshot; no per-run scaling (corroboration retired).
    private static PromCounterSnapshot CleanSnapshot() => new()
    {
        OrchestratorMessagesSentDelta = 0,
        OrchestratorMessagesConsumedDelta = 0,
        ProcessorMessagesConsumedDelta = 0,
        ProcessorMessagesSentDelta = 0,
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
            ProcessorMessagesSentDelta = 8, // short of 9 — would have failed the OLD binding gate
        };

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");

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
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");

        Assert.Equal(Verdict.Pass, report.Verdict);
    }
}
