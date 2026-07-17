using Xunit;

namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// Hermetic regression facts for <see cref="StructuralCohort"/> (Phase 78 D-04 gap closure). These pin the
/// class of bug that was INVISIBLE under the one-record-per-stepId <see cref="RunTrace.FromStepIds"/> helper:
/// post-Phase-77, each genuine hop emits MULTIPLE framework records sharing one <c>{executionId, stepId}</c>
/// (the processor <c>"hop executed"</c> consume record PLUS the <c>"result sent"</c> send record PLUS the
/// orchestrator <c>"fan-out"</c> dispatch record). The 78-04 live gate exposed that the old
/// <c>hasMessageId</c> discriminator counted ALL of them → each hop repeated 3–4× → <c>Duplicates ==
/// StartedRuns</c> on every run (a false effect-once fail on the no-fault baseline). Because the old hermetic
/// facts synthesized exactly ONE record per stepId, the gap only surfaced live.
///
/// <para>
/// These facts feed the EXACT multi-record-per-hop shape (per the 78-04 evidence: ~30 StepId records over 10
/// distinct stepIds, each hop 3×) through <see cref="StructuralCohort.Classify"/> — the SAME classifier the
/// live fixture (<c>AnalyzerE2ETests.BuildRunTraces</c>) now delegates to — so the collapse is provable
/// WITHOUT a live stack this time. They also pin that a genuine redelivery (two <c>"hop executed"</c> records
/// for one <c>{executionId, stepId}</c>) STILL trips <see cref="RunTrace.HasIllegitimateDuplicate"/> → the
/// fix collapses by record-KIND selection, NOT to presence-only — it does NOT blind the genuine
/// effect-once/duplicate detection (per D-04 gap: the forbidden naive collapse is rejected).
/// </para>
/// </summary>
public sealed class StructuralCohortFacts
{
    // Ten distinct non-convergent stepIds — the 78-04 live evidence shape (one no-fault execution had 30
    // StepId records over 10 distinct stepIds, each hop 3×). Non-convergent so ANY repeat is illegitimate.
    private static readonly string[] TenDistinctStepIds =
        { "hop-a", "hop-b", "hop-c", "hop-d", "hop-e", "hop-f", "hop-g1", "hop-h", "hop-i", "hop-j" };

    private const string Corr = "corr-1";
    private const string Exec = "exec-1";
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    // ── Synthetic framework-record builders (the live body.text shapes) ─────────────────────────────────
    // The canonical CONSUME "did-run" record — the 1-per-genuine-execution anchor (ProcessorPipeline.cs:218).
    private static FrameworkLogRecord Consume(string step, string mid) =>
        new(Corr, Exec, step, mid, $"hop executed {mid} Completed", T0);
    // The processor result SEND record (OutputTail.cs:94) — carries {StepId,ExecutionId,MessageId} post-D3,
    // so it matches the structural query, but is NOT a did-run hop → must be IGNORED by Classify.
    private static FrameworkLogRecord ResultSent(string step, string mid) =>
        new(Corr, Exec, step, mid, $"result sent {mid} Completed", T0);
    // The orchestrator fan-out dispatch SEND record (OrchestratorPrePipeline.cs:170) — same story, IGNORED.
    private static FrameworkLogRecord FanOut(string step, string mid, string next) =>
        new(Corr, Exec, step, mid, $"fan-out {mid} {next}", T0);
    // The orchestrator terminal-reached record (OrchestratorPrePipeline.cs:107) — no MessageId → PROVEN, not observed.
    private static FrameworkLogRecord Terminal(string step) =>
        new(Corr, Exec, step, MessageId: null, "terminal reached", T0);

    // A clean Prom snapshot (result conservation holds, keeper probe live) so the metric gate never confounds
    // the structural branch — mirrors PassFailEngineFacts.CleanSnapshot().
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

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Expect(string key, IEnumerable<string> steps) =>
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [key] = new HashSet<string>(steps, StringComparer.Ordinal),
        };

    /// <summary>
    /// The multi-record-per-hop live shape (per D-04 gap): 10 distinct stepIds, each emitting its
    /// <c>"hop executed"</c> consume record PLUS a <c>"result sent"</c> record PLUS a <c>"fan-out"</c> record
    /// (30 records, mirroring the 78-04 signature) — all sharing one <c>{executionId, stepId}</c>. Classify
    /// must collapse to ONE observed hop per stepId (no false duplicate), so the run is complete + duplicate-free
    /// → <see cref="Verdict.Pass"/>. This fact FAILS against pre-fix code (counts 3× → false
    /// <c>Duplicates == StartedRuns</c>) and PASSES after — the regression is now hermetic.
    /// </summary>
    [Fact]
    public void NoFault_MultiRecordPerHop_Collapses_ToOneObservedHop_Pass()
    {
        var records = new List<FrameworkLogRecord>();
        var mid = 0;
        foreach (var step in TenDistinctStepIds)
        {
            records.Add(Consume(step, $"m-consume-{mid}"));       // the canonical did-run record
            records.Add(ResultSent(step, $"m-sent-{mid}"));       // send-side noise (post-Phase-77) — must be ignored
            records.Add(FanOut(step, $"m-fanout-{mid}", "next")); // dispatch-side noise — must be ignored
            mid++;
        }
        Assert.Equal(30, records.Count); // the 78-04 shape: 3 framework records per hop

        var cohort = StructuralCohort.Classify(records);
        var observed = cohort.StepIdsByInstance[(Corr, Exec)];

        // THE COLLAPSE: each stepId observed EXACTLY ONCE — the send/fan-out records contributed nothing.
        Assert.Equal(10, observed.Count);
        Assert.Equal(observed.Count, observed.Distinct().Count()); // no illegitimate repeat

        var run = RunTrace.FromStepIds(Corr, Exec, observed);
        Assert.False(run.HasIllegitimateDuplicate);

        var report = new PassFailEngine().Analyze(new[] { run }, CleanSnapshot(), "unit-test",
            expectedStepIdsByExecution: Expect($"{Corr}|{Exec}", TenDistinctStepIds));

        Assert.Equal(1, report.StartedRuns);
        Assert.Equal(1, report.CompleteRuns);
        Assert.Equal(0, report.Missing);
        Assert.Empty(report.Duplicates);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    /// <summary>
    /// The fix must NOT blind genuine effect-once detection (the forbidden collapse-to-presence-only outcome,
    /// per D-04 gap). Same multi-record run, but ONE non-convergent stepId emits its <c>"hop executed"</c>
    /// consume record TWICE (a genuine redelivery — two REAL executions), alongside the normal send/fan-out
    /// noise. Classify RETAINS the duplicate stepId → <see cref="RunTrace.HasIllegitimateDuplicate"/> true →
    /// non-empty <c>Duplicates</c> → <see cref="Verdict.Fail"/>.
    /// </summary>
    [Fact]
    public void GenuineRedelivery_TwoConsumeRecords_ForOneStep_TripsDuplicate()
    {
        var records = new List<FrameworkLogRecord>();
        var mid = 0;
        foreach (var step in TenDistinctStepIds)
        {
            records.Add(Consume(step, $"m-consume-{mid}"));
            records.Add(ResultSent(step, $"m-sent-{mid}"));
            records.Add(FanOut(step, $"m-fanout-{mid}", "next"));
            mid++;
        }
        // GENUINE redelivery: "hop-c" was really consumed a SECOND time (a distinct delivery-unit MessageId).
        records.Add(Consume("hop-c", "m-consume-redelivery"));

        var cohort = StructuralCohort.Classify(records);
        var observed = cohort.StepIdsByInstance[(Corr, Exec)];

        // The genuine duplicate SURVIVED the collapse (record-KIND selection, not presence-only).
        Assert.Equal(2, observed.Count(s => s == "hop-c"));

        var run = RunTrace.FromStepIds(Corr, Exec, observed);
        Assert.True(run.HasIllegitimateDuplicate);

        var report = new PassFailEngine().Analyze(new[] { run }, CleanSnapshot(), "unit-test",
            expectedStepIdsByExecution: Expect($"{Corr}|{Exec}", TenDistinctStepIds));

        Assert.NotEmpty(report.Duplicates);
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    /// <summary>
    /// A <c>"terminal reached"</c> record (no MessageId) feeds the ANL-03 proven set, NOT the observed did-run
    /// stepId list — the fix narrows proven to genuine terminal-reached records only (per D-04 gap).
    /// </summary>
    [Fact]
    public void TerminalReached_Record_FeedsProven_NotObserved()
    {
        var records = new List<FrameworkLogRecord>
        {
            Consume("hop-a", "m-consume-0"),   // an observed did-run hop
            Terminal("hop-term"),              // terminal-reached — proven, not observed
        };

        var cohort = StructuralCohort.Classify(records);
        var observed = cohort.StepIdsByInstance[(Corr, Exec)];

        Assert.Contains("hop-a", observed);
        Assert.DoesNotContain("hop-term", observed);              // NOT an observed hop
        Assert.Contains("hop-term", cohort.Proven[$"{Corr}|{Exec}"]); // IS ANL-03 proven evidence
    }
}
