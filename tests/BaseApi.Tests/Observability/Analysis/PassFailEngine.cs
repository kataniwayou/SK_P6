namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// The pure correctness arbiter (OBS-01/02/03), re-founded on the ES-binding model (67-03). A plain
/// object — NO ES/Prom/Http/host dependency, NO IO — so every decision branch is provable with
/// synthetic <see cref="RunTrace"/> + <see cref="PromCounterSnapshot"/> inputs (66-VALIDATION.md).
/// The RealStack fixture (Plan 03) feeds it real parsed inputs; the hermetic facts feed synthetic
/// ones so a green RealStack run is trustworthy rather than vacuously green.
///
/// <para>
/// <b>Binding arbiter = ES (per-run), 67-03.</b> Aligns the engine with the fixture's documented
/// design: "ES-primary completeness is the binding arbiter; Prom reconciliation is corroborating
/// only." The previous engine conflated three Prom counters into the binding pass gate
/// (triggerCount = DispatchSentDelta as the per-run denominator; ResultSentCompletedDelta ≥
/// complete × 9; |DispatchSentDelta − triggerCount| as a gate). The orchestrator emits one dispatch
/// per STEP, so DispatchSentDelta is ~9× the run count — using it as the per-run denominator made a
/// perfectly-complete 10-run window score 71 "missing" (81 = 9×9 vs ES 10). Those three are retired
/// from the binding gate; they survive only as corroboration math.
/// </para>
///
/// <para>
/// <b>Decision logic (ES-binding):</b>
/// <list type="number">
/// <item>STARTED (denominator): distinct (correlationId, executionId) instances with ≥1 Step_* log —
///   i.e. <c>runs.Count</c> (the fixture builds one <see cref="RunTrace"/> per (correlationId, executionId)
///   pair that emitted any Step_* hit; each spawned execution is its own run).</item>
/// <item>COMPLETE (OBS-01): a started run whose distinct StepLabel set equals the full 9-label set
///   (necessarily both sinks Step_F1 + Step_F2).</item>
/// <item>MISSING (OBS-02): <c>StartedRuns − CompleteRuns</c> — started-but-incomplete (1–8 labels);
///   &gt; 0 ⇒ Fail. A FULLY-dead run (dispatched but never logging Step_A) never started in ES, so it
///   is NOT in this count — it surfaces via the Prom corroboration warning instead.</item>
/// <item>DUPLICATE (OBS-02, fail-closed, BINDING): ANY duplicate (correlationId, StepLabel) ⇒ Fail —
///   the live dedupe counters are dormant, so no redelivery can be corroborated.</item>
/// <item>PROM CORROBORATION (OBS-03, NON-BINDING, 67-03): compute impliedRuns =
///   round(DispatchSentDelta / 9) and compare to StartedRuns within a ±1-run boundary tolerance. A
///   positive excess beyond tolerance (impliedRuns − StartedRuns &gt; tolerance) — a dispatched run
///   ES never observed — is a WARNING, NOT a Fail. Any non-completed terminal outcome is also a
///   warning. The documented ~1-run window-edge mismatch (81 = 9×9 vs ES 10) is inside tolerance.</item>
/// <item>VERDICT: Pass iff (every started run complete) AND (no duplicate). Prom corroboration is
///   non-fatal — it never flips a green ES verdict.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PassFailEngine
{
    /// <summary>
    /// The 10-label completeness set (verbatim — FanOutSeederE2ETests.cs NodeNumbers keys):
    /// A→B→C→{D1→E1→F1, D2→E2→F2}→G, with the single shared convergent terminal Step_G reachable from
    /// BOTH sinks (73, D-10). A COMPLETE run's DISTINCT set equals this exactly (10 distinct labels) — note
    /// Step_G logs TWICE per run (the per-arrival fan-in), which inflates RAW count to 11 but DISTINCT to 10.
    /// </summary>
    private static readonly HashSet<string> AllLabels = new(StringComparer.Ordinal)
    { "Step_A", "Step_B", "Step_C", "Step_D1", "Step_E1", "Step_F1", "Step_D2", "Step_E2", "Step_F2", "Step_G" };

    /// <summary>
    /// The Prom dispatch-count basis for the (inert, non-binding) corroboration math — kept at 9, NOT 10.
    /// The convergent Step_G's ×2 fan-in is intentionally NOT folded into this count: metrics are DEFERRED
    /// out of this phase (SPEC out-of-scope — no metric-counter assertions), and the BINDING verdict is
    /// value-chain + completeness, NEVER Prom. Folding Step_G's ×2 here would distort the inert
    /// promImpliedRuns / spawnReconSlack arithmetic without protecting any binding outcome (73, D-10). The
    /// corroboration block stays structurally intact and can never gate the value-chain verdict.
    /// </summary>
    public const int LabelsPerRun = 9;

    /// <summary>
    /// Boundary tolerance (in RUNS) for the Prometheus corroboration cross-check. The orchestrator
    /// emits one dispatch per step, so a window-edge run can be counted by Prom but fall just outside
    /// the ES range (or vice versa) — the documented ~1-run Prom/ES window-edge mismatch (e.g. Prom
    /// 81 = 9×9 ⇒ impliedRuns 9 vs ES StartedRuns 10). A ±1-run tolerance prevents that boundary
    /// artifact from raising a spurious corroboration warning. (67-03 — replaces the old 0.5-count
    /// DeltaTolerance, which operated on the now-retired binding delta arithmetic.)
    /// </summary>
    public const int CorroborationRunTolerance = 1;

    /// <summary>
    /// Score a set of per-correlationId traces against the live Prometheus counter deltas, producing
    /// the single per-scenario <see cref="AnalyzerReport"/>. PURE: no IO — the caller (fixture) writes
    /// the report.
    /// </summary>
    /// <param name="runs">
    /// One <see cref="RunTrace"/> per distinct correlationId that emitted ≥1 Step_* log — the
    /// ES-binding STARTED set (the denominator).
    /// </param>
    /// <param name="prom">The windowed Prometheus counter deltas — corroboration evidence only (67-03).</param>
    /// <param name="triggerCount">
    /// The dispatch-derived count (round(DispatchSentDelta)) — kept as Prom corroboration evidence,
    /// NOT the binding denominator. The orchestrator dispatches per step, so this is ~9× the run count.
    /// </param>
    /// <param name="scenarioId">The scenario id for the report + path.</param>
    /// <param name="spawnExtra">
    /// SPAWN-AWARE OBS-03 corroboration (non-binding): the number of EXTRA results the entry fan-out emits
    /// beyond the dispatch count. The entry step now spawns 2 results from 1 dispatch, so
    /// <c>ResultConsumedDelta ≈ DispatchSentDelta + spawnExtra</c> where <c>spawnExtra</c> = the number of
    /// entry dispatches = cron fires = distinct correlationIds. Derived from data by the caller (the fixture
    /// passes <c>traces.Select(t =&gt; t.CorrelationId).Distinct().Count()</c>) — NEVER hard-coded. Default 0
    /// keeps every pre-spawn caller's behaviour identical.
    /// </param>
    /// <param name="tripDurationMsByExecution">
    /// VALUE-CHAIN/TRIP evidence (73, D-12): per-<c>(corr, exec)</c> trip duration ms keyed
    /// <c>"correlationId|executionId"</c> (first-step → Step_G-terminal ES <c>@timestamp</c> delta). The engine
    /// reads NO ES timestamps; the live fixture (Plan 04) passes the real map, the hermetic facts pass
    /// synthetic/empty. Default null ⇒ empty map (keeps existing callers compiling, no behaviour change).
    /// </param>
    /// <param name="tripDurationMsByCorrelation">Per-<c>correlationId</c> aggregate trip duration ms (73, D-12). Default null ⇒ empty.</param>
    /// <param name="seedsByExecution">
    /// VALUE-CHAIN seed map (73, D-11): per-<c>(corr, exec)</c> seed keyed <c>"correlationId|executionId"</c>, used to
    /// check each label's surfaced value equals <c>seed + hop-count</c>. When a run has no entry here, its seed is
    /// recovered from the chain itself (<c>Values["Step_B"] - 1</c>). Default null ⇒ recover-from-chain for all.
    /// </param>
    public AnalyzerReport Analyze(IReadOnlyList<RunTrace> runs, PromCounterSnapshot prom,
                                  int triggerCount, string scenarioId, int spawnExtra = 0,
                                  IReadOnlyDictionary<string, double>? tripDurationMsByExecution = null,
                                  IReadOnlyDictionary<string, double>? tripDurationMsByCorrelation = null,
                                  IReadOnlyDictionary<string, int>? seedsByExecution = null)
    {
        // ── ES-BINDING ARBITER (67-03) ────────────────────────────────────────────────────────────

        // STARTED (denominator): distinct (correlationId, executionId) instances with ≥1 Step_* log = one
        // RunTrace each (each spawned execution is its own run).
        var startedRuns = runs.Count;

        // COMPLETE (OBS-01): distinct StepLabel set equals the full 10-label set (both sinks + the
        // convergent terminal Step_G). Step_G logs ×2 per run but DISTINCT collapses it to one (73, D-10).
        var complete = runs.Where(r => r.DistinctLabels.SetEquals(AllLabels)).ToList();

        // MISSING (OBS-02): started-but-incomplete. Bound against the ES STARTED denominator — NOT the
        // Prom dispatch count. A fully-dead run (never started in ES) is invisible here and surfaces
        // only in the Prom corroboration warning below.
        var missing = startedRuns - complete.Count;
        var missingDetail = new List<string>();
        if (missing > 0)
        {
            missingDetail.Add(
                $"{missing} of {startedRuns} STARTED run(s) (distinct correlationId with ≥1 Step_* log) did NOT reach " +
                "COMPLETE (all 10 labels incl. both sinks Step_F1 + Step_F2 and the convergent terminal Step_G).");
            missingDetail.Add(
                "A fully-dead run (dispatched but never logging Step_A) never started in ES and is NOT in this count; " +
                "it surfaces as a Prom corroboration WARNING (impliedRuns > startedRuns). The specific missing " +
                "correlationId for such a run is NOT recoverable from telemetry (research item #1).");
        }

        // DUPLICATE (OBS-02, fail-closed, BINDING): any ILLEGITIMATE duplicate (correlationId, StepLabel) is a
        // FAIL. Reads the convergent-aware HasIllegitimateDuplicate (73, D-10), NOT the raw HasAnyDuplicateLabel,
        // so the legitimate Step_G ×2 fan-in does NOT trip the rule WHILE every other duplicate AND a Step_G
        // count ≠ 2 (1 = missing arrival, 3+ = same-entryId redelivery) still fail closed. No live dedupe counter
        // can corroborate a redelivery (dormant) → un-corroboratable → fail-closed.
        var duplicates = runs.Where(r => r.HasIllegitimateDuplicate).ToList();
        var dupFail = duplicates.Count > 0;

        // VALUE-CHAIN ASSERTION (NEW, 73, D-11, BINDING): each STARTED run's surfaced values must follow the
        // deterministic chain Values[label] == seed + hop-count (B=seed+1 … G=seed+6), and BOTH Step_G arrivals
        // sit at the shared terminal seed+6 — the LIVE terminal-anchor proxy (the Step_G completed-terminal ES
        // log carrying seed+6). The auditor does NOT read a Redis skp:out: blob; that durable-blob proof is owned
        // by the hermetic harness (Plan 02). A run is checked ONLY if it surfaced any values (legacy callers that
        // pass no value map are skipped, so existing behaviour is unchanged). valueChainOk is true when EVERY
        // checked run's chain holds; it folds into the verdict.
        var valueChainDetail = new List<string>();
        var valueChainOk = true;
        foreach (var run in runs)
        {
            if (run.Values.Count == 0)
            {
                continue; // no surfaced values (legacy caller) → not value-chain-checked
            }

            var seed = ResolveSeed(run, seedsByExecution);
            if (!CheckValueChain(run, seed, out var detail))
            {
                valueChainOk = false;
            }
            valueChainDetail.Add(detail);
        }

        var tripByExec = tripDurationMsByExecution ?? new Dictionary<string, double>(StringComparer.Ordinal);
        var tripByCorr = tripDurationMsByCorrelation ?? new Dictionary<string, double>(StringComparer.Ordinal);

        // ── PROM CORROBORATION (OBS-03, NON-BINDING, 67-03) ────────────────────────────────────────
        // The three retired conflations (#1 DispatchSentDelta as per-run denom; #2 ResultSentCompleted
        // ≥ complete × 9 as binding; #3 |DispatchSentDelta − triggerCount| as binding) survive ONLY as
        // corroboration math here — they never gate the verdict.
        //
        //   impliedRuns = round(DispatchSentDelta / 9): the run count IMPLIED by the per-step dispatch
        //   counter. A positive excess over StartedRuns beyond tolerance means Prom saw more runs
        //   dispatched than ES observed start — i.e. a fully-dead run. WARNING, not a fail.
        // Math.Round defaults to MidpointRounding.ToEven (banker's rounding). A half-step delta is
        // already pathological (DispatchSentDelta is an integer counter delta, so /9 lands on a half only
        // for non-multiples), and the ±1-run CorroborationRunTolerance absorbs any single-run rounding
        // wobble — corroboration never gates the verdict — so ToEven is intentionally accepted here (IN-01).
        var promImpliedRuns = (int)Math.Round(prom.DispatchSentDelta / LabelsPerRun);
        var corroborationDetail = new List<string>();

        // ASYMMETRY (intentional, 67-03 / WR-01): only the POSITIVE direction (Prom implies MORE
        // dispatched runs than ES observed STARTED) raises a warning, because that is precisely the
        // dead-run signal this harness is built to detect (a run dispatched but never logging Step_A).
        // The negative direction (ES STARTED > Prom implied — a dispatch under-count, a scrape gap, or
        // ES double-counting correlationIds) is deliberately NOT a warning here: it cannot indicate a
        // dead run, and Prom is corroboration-only (it never gates the verdict), so flagging it would
        // add noise without protecting the binding outcome. If a future need arises to surface telemetry
        // under-counting, guard `Math.Abs(deadRunExcess)` with a distinct, clearly-labelled message — do
        // NOT fold it into this dead-run warning.
        var deadRunExcess = promImpliedRuns - startedRuns;
        if (deadRunExcess > CorroborationRunTolerance)
        {
            corroborationDetail.Add(
                $"Prom corroboration WARNING: DispatchSentDelta={prom.DispatchSentDelta} ⇒ ~{promImpliedRuns} dispatched run(s), " +
                $"but ES observed only {startedRuns} STARTED run(s) (excess {deadRunExcess} > ±{CorroborationRunTolerance} tolerance). " +
                "This is how a fully-dead run (dispatched, Step_A never logged) surfaces. NON-FATAL (Prom is corroborating only, 67-03).");
        }

        // Terminal non-completed processor outcomes (failed/cancelled/processing) are corroboration
        // evidence (D-08) — surfaced as a WARNING, no longer fail-closed binding.
        var nonCompletedOutcomes = prom.NonCompletedOutcomes.Where(kv => kv.Value != 0).ToList();
        if (nonCompletedOutcomes.Count > 0)
        {
            var detail = string.Join(", ", nonCompletedOutcomes.Select(kv => $"{kv.Key}={kv.Value}"));
            corroborationDetail.Add(
                $"Prom corroboration WARNING: non-completed terminal outcome(s) observed ({detail}). " +
                "NON-FATAL (Prom is corroborating only, 67-03).");
        }

        // SPAWN-AWARE OBS-03 (NON-BINDING): the entry step now emits 2 results from 1 dispatch, so the
        // result counter runs AHEAD of the dispatch counter by exactly one extra result per entry dispatch.
        // Reconcile ResultConsumedDelta against DispatchSentDelta + spawnExtra (spawnExtra = entry-dispatch
        // count = distinct correlationIds, derived from data by the caller — never hard-coded). The excess is
        // measured in RESULT units; allow the same ±1-run boundary slack (CorroborationRunTolerance × 9
        // result-emitting steps) so a window-edge run does not raise a spurious warning. A mismatch beyond
        // that slack is a WARNING only — it never flips a green ES verdict.
        var expectedResultConsumed = prom.DispatchSentDelta + spawnExtra;
        var spawnReconExcess = prom.ResultConsumedDelta - expectedResultConsumed;
        var spawnReconSlack = CorroborationRunTolerance * LabelsPerRun;
        if (Math.Abs(spawnReconExcess) > spawnReconSlack)
        {
            corroborationDetail.Add(
                $"Prom corroboration WARNING (spawn-aware OBS-03): ResultConsumedDelta={prom.ResultConsumedDelta} " +
                $"vs expected DispatchSentDelta({prom.DispatchSentDelta}) + spawnExtra({spawnExtra}) = {expectedResultConsumed} " +
                $"(off by {spawnReconExcess}, beyond ±{spawnReconSlack} result-step slack). The entry fan-out emits one " +
                "extra result per entry dispatch; an unexpected gap means a result/dispatch imbalance. " +
                "NON-FATAL (Prom is corroborating only, 67-03).");
        }

        var recon = corroborationDetail.Count == 0
            ? ReconciliationOutcome.Reconciled
            : ReconciliationOutcome.Unreconciled;

        // ── VERDICT (ES-binding; Prom corroboration is non-fatal) ──────────────────────────────────
        // The value-chain check (incl. the Step_G-at-seed+6 terminal-anchor proxy) is BINDING (73, D-11);
        // Prom corroboration remains non-fatal and never flips a green ES verdict.
        var pass = missing == 0 && !dupFail && valueChainOk;
        var verdict = pass ? Verdict.Pass : Verdict.Fail;

        // Build the report (no IO).
        return new AnalyzerReport
        {
            ScenarioId = scenarioId,
            Verdict = verdict,
            StartedRuns = startedRuns,
            TriggerCount = triggerCount,
            CompleteRuns = complete.Count,
            Missing = missing,
            MissingDetail = missingDetail,
            Duplicates = duplicates,
            PromImpliedRuns = promImpliedRuns,
            SpawnExtra = spawnExtra,
            ExpectedResultConsumed = expectedResultConsumed,
            Reconciliation = recon,
            CorroborationDetail = corroborationDetail,
            Prom = prom,
            Traces = runs,
            ValueChainOk = valueChainOk,
            ValueChainDetail = valueChainDetail,
            TripDurationMsByExecution = tripByExec,
            TripDurationMsByCorrelation = tripByCorr,
            HumanSummary = BuildSummary(
                scenarioId, verdict, startedRuns, complete.Count, missing, dupFail, valueChainOk, recon, corroborationDetail),
        };
    }

    /// <summary>
    /// The expected per-hop value offset from the run's seed (73, D-11/D-02): the deterministic chain
    /// seed → B=seed+1, C=seed+2, {D1,D2}=seed+3, {E1,E2}=seed+4, {F1,F2}=seed+5, G=seed+6 (both Step_G
    /// arrivals share the terminal seed+6). Step_A is the seed source itself (offset 0). Symmetric branches
    /// carry the same per-hop value, distinguished only by their framework-owned entryId (D-07).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> ExpectedHopOffset =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Step_A"] = 0,
            ["Step_B"] = 1,
            ["Step_C"] = 2,
            ["Step_D1"] = 3, ["Step_D2"] = 3,
            ["Step_E1"] = 4, ["Step_E2"] = 4,
            ["Step_F1"] = 5, ["Step_F2"] = 5,
            ["Step_G"] = 6,
        };

    /// <summary>
    /// Resolve the seed for a run's value chain (73, D-11): the explicit <paramref name="seedsByExecution"/>
    /// entry keyed <c>"correlationId|executionId"</c> if present, else recovered from the chain itself
    /// (<c>Values["Step_B"] - 1</c>), else <c>Values["Step_A"]</c> (the seed source), else 0.
    /// </summary>
    private static int ResolveSeed(RunTrace run, IReadOnlyDictionary<string, int>? seedsByExecution)
    {
        if (seedsByExecution is not null &&
            seedsByExecution.TryGetValue($"{run.CorrelationId}|{run.ExecutionId}", out var seed))
        {
            return seed;
        }
        if (run.Values.TryGetValue("Step_B", out var b))
        {
            return b - 1;
        }
        if (run.Values.TryGetValue("Step_A", out var a))
        {
            return a;
        }
        return 0;
    }

    /// <summary>
    /// Check a run's deterministic value chain (73, D-11): for every surfaced label, assert
    /// <c>Values[label] == seed + ExpectedHopOffset[label]</c>. Both Step_G arrivals share the single map
    /// entry at the terminal <c>seed + 6</c> — the LIVE terminal-anchor proxy. Emits a per-run evidence line
    /// (expected-vs-actual) and returns false on the first mismatch (recorded in the detail).
    /// </summary>
    private static bool CheckValueChain(RunTrace run, int seed, out string detail)
    {
        var mismatches = new List<string>();
        foreach (var (label, actual) in run.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!ExpectedHopOffset.TryGetValue(label, out var offset))
            {
                continue; // unknown label carries no chain expectation
            }
            var expected = seed + offset;
            if (actual != expected)
            {
                mismatches.Add($"{label} expected {expected} got {actual}");
            }
        }

        var ok = mismatches.Count == 0;
        detail = ok
            ? $"[{run.CorrelationId}|{run.ExecutionId}] value-chain OK (seed {seed}; Step_G terminal {seed + 6})."
            : $"[{run.CorrelationId}|{run.ExecutionId}] value-chain FAIL (seed {seed}): {string.Join(", ", mismatches)}.";
        return ok;
    }

    private static string BuildSummary(string scenarioId, Verdict verdict, int startedRuns,
        int completeRuns, int missing, bool dupFail, bool valueChainOk, ReconciliationOutcome recon,
        IReadOnlyList<string> corroborationDetail)
    {
        var reasons = new List<string>();
        if (missing > 0) reasons.Add($"{missing} started-but-incomplete");
        if (dupFail) reasons.Add("illegitimate duplicate (fail-closed)");
        if (!valueChainOk) reasons.Add("value-chain mismatch (seed + hop-count / Step_G terminal anchor)");

        var driver = verdict == Verdict.Pass
            ? "every started run complete, no illegitimate duplicate, value-chain intact (ES-binding)"
            : string.Join("; ", reasons);

        // Prom corroboration is reported alongside the (ES-binding) verdict, never as its cause.
        var corroboration = recon == ReconciliationOutcome.Reconciled
            ? "Prom corroboration clean"
            : $"Prom corroboration WARNING [{corroborationDetail.Count}] (non-fatal)";

        return $"[{scenarioId}] {verdict}: {completeRuns}/{startedRuns} started runs complete — {driver}; {corroboration}.";
    }
}
