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
/// (triggerCount = OrchestratorMessagesSentDelta as the per-run denominator; ProcessorMessagesSentDelta ≥
/// complete × 9; |OrchestratorMessagesSentDelta − triggerCount| as a gate). The orchestrator emits one dispatch
/// per STEP, so OrchestratorMessagesSentDelta is ~9× the run count — using it as the per-run denominator made a
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
///   is NOT in this count.</item>
/// <item>DUPLICATE (OBS-02, fail-closed, BINDING): ANY duplicate (correlationId, StepLabel) ⇒ Fail —
///   the live dedupe counters are dormant, so no redelivery can be corroborated.</item>
/// <item>METRIC GATE (BINDING): the former Prom corroboration math (impliedRuns = round(sent/9),
///   spawn-aware result reconciliation) was retired as miscalibrated and replaced by three gates evaluated
///   at quiescence — MG-1 result conservation (orchestrator_consumed == processor_sent, per-scenario
///   binding via <paramref name="mg1Binding"/>; reporting-only where a tier restart resets a counter or an
///   L2 wipe causes tolerated loss), MG-2 keeper-recovery activity (per-scenario via
///   <paramref name="expectsKeeperActivity"/>), and MG-3 keeper-l2-probe liveness (universal). A failing
///   BINDING gate flips the verdict and emits a CorroborationDetail line (Reconciliation=Unreconciled).</item>
/// <item>VERDICT: Pass iff (every started run complete) AND (no duplicate) AND
///   (the binding metric gate holds). (D-03/Phase 78: the concrete value chain is deleted — structural
///   stepId completeness + ANL-03 redundancy + the metric gate are the sole axes.)</item>
/// </list>
/// </para>
/// </summary>
public sealed class PassFailEngine
{
    // ── ANL-01 / D-15: the old StepLabel-keyed structural hop-set constant is DELETED (clean break). Structural
    // completeness is now stepId-keyed (RunTrace.DistinctStepIds) against the ES-derived expected set (FW-02
    // dispatch NextStepIds) — see the `ExpectedFor`/`RunComplete` locals in Analyze. The ONLY canonical hop
    // set that survives is the value oracle's own `ExpectedHopOffset` (label→depth, KEPT per D-16), which the
    // completeness fallback derives its legacy value-oracle-run shape from — never a second structural constant.

    /// <summary>The empty stepId proven-set reused when a run has no orchestrator-redundancy evidence (ANL-03).</summary>
    private static readonly IReadOnlySet<string> EmptyStepSet = new HashSet<string>(StringComparer.Ordinal);

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
    /// <param name="scenarioId">The scenario id for the report + path.</param>
    /// <param name="tripDurationMsByExecution">
    /// VALUE-CHAIN/TRIP evidence (73, D-12): per-<c>(corr, exec)</c> trip duration ms keyed
    /// <c>"correlationId|executionId"</c> (first-step → Step_G-terminal ES <c>@timestamp</c> delta). The engine
    /// reads NO ES timestamps; the live fixture (Plan 04) passes the real map, the hermetic facts pass
    /// synthetic/empty. Default null ⇒ empty map (keeps existing callers compiling, no behaviour change).
    /// </param>
    /// <param name="tripDurationMsByCorrelation">Per-<c>correlationId</c> aggregate trip duration ms (73, D-12). Default null ⇒ empty.</param>
    /// <param name="keeperOutcomeByExecution">
    /// RECOVERABILITY evidence (75, D75-3/D75-4): per-<c>(corr, exec)</c> keeper REINJECT outcome keyed
    /// <c>"correlationId|executionId"</c> → outcome string (<c>"drop"</c> for a keeper-confirmed clean-absent DROP
    /// = provably-unrecoverable; <c>"reinject"</c> for a recovered reinject). A started-but-incomplete run whose
    /// key maps to <c>"drop"</c> is TOLERATED (non-binding, cause-labeled) — the keeper proved the L2 data was
    /// physically gone, so it was unrecoverable by design. Everything NOT keeper-drop-tolerated and NOT on the
    /// redis-wipe timestamp path (D75-5) is a recoverable-but-lost BINDING miss. The engine reads NO ES; the live
    /// fixture (Plan 03/04) passes the real map, the hermetic facts pass synthetic. Default null ⇒ empty map
    /// (keeps existing callers compiling — the redis-wipe timestamp path alone then governs tolerance, unchanged).
    /// </param>
    /// <param name="expectedStepIdsByExecution">
    /// STRUCTURAL expected set (Phase 76, ANL-02): per-<c>(corr, exec)</c> set of stepIds the orchestrator FW-02
    /// fan-out records dispatched, keyed <c>"correlationId|executionId"</c>. A run is COMPLETE when its
    /// <see cref="RunTrace.DistinctStepIds"/> covers its expected set; a dispatched stepId with no matching
    /// processor record is a binding miss (dispatched-but-never-executed — TEST-08 closes). Derived from ES
    /// records ONLY (no Postgres/graph — T-76-07). Default null ⇒ the per-run fallback: the value oracle's
    /// canonical hop set for a legacy label-carrying run, else the union of observed stepIds across the cohort
    /// (keeps existing non-ANL-02 callers compiling + green).
    /// </param>
    /// <param name="orchestratorConsumedStepIdsByExecution">
    /// FRAMEWORK-REDUNDANCY evidence (Phase 76, ANL-03): per-<c>(corr, exec)</c> set of stepIds the orchestrator
    /// independently witnessed as having-run (its output EntryId M_N was consumed by a fan-out/terminal record),
    /// keyed <c>"correlationId|executionId"</c>. A run missing an expected stepId's processor record but whose
    /// EVERY missing stepId is in this set is reconciled as a NON-binding telemetry gap — WITH NO seed oracle
    /// (the ANL-03 acceptance). A stepId absent from BOTH the processor records AND this set stays a binding
    /// miss. Default null ⇒ no redundancy evidence (a dropped record is a binding miss unless another tolerance
    /// path applies).
    /// </param>
    public AnalyzerReport Analyze(IReadOnlyList<RunTrace> runs, PromCounterSnapshot prom,
                                  string scenarioId,
                                  IReadOnlyDictionary<string, double>? tripDurationMsByExecution = null,
                                  IReadOnlyDictionary<string, double>? tripDurationMsByCorrelation = null,
                                  bool expectsKeeperActivity = false,
                                  bool mg1Binding = true,
                                  DateTimeOffset? recoveryUtc = null,
                                  IReadOnlyDictionary<string, DateTimeOffset>? firstHopUtcByExecution = null,
                                  IReadOnlyDictionary<string, DateTimeOffset>? lastHopUtcByExecution = null,
                                  IReadOnlyDictionary<string, string>? keeperOutcomeByExecution = null,
                                  IReadOnlyDictionary<string, IReadOnlySet<string>>? expectedStepIdsByExecution = null,
                                  IReadOnlyDictionary<string, IReadOnlySet<string>>? orchestratorConsumedStepIdsByExecution = null)
    {
        // ── ES-BINDING ARBITER (67-03) + STEPID-KEYED STRUCTURAL COMPLETENESS (Phase 76, ANL-01/02) ──────

        // D-01 (Phase 78): the Mode-2 entry marker no longer carries an ExecutionId (empty GUID skipped by
        // ExecutionLogScope), so the fixture's `exists attributes.ExecutionId` ES query filter drops it upstream
        // — no engine-level entry-marker predicate is needed. Every run reaching the engine is a real scored run.
        var scored = runs.ToList();

        // STARTED (denominator): distinct (correlationId, executionId) instances with ≥1 framework hop record =
        // one RunTrace each (each spawned execution is its own run). D75-1 LOCKED: the verdict is founded on this
        // per-(corr,exec) started denominator, never on any absolute count or window.
        var startedRuns = scored.Count;

        // ── EXPECTED-SET RESOLUTION (ANL-02) ──────────────────────────────────────────────────────────────
        // Structural completeness is stepId-keyed against the per-(corr,exec) EXPECTED set. Priority:
        //   1. explicit ES-derived FW-02 dispatch set (expectedStepIdsByExecution) — the ANL-02 binding path;
        //   2. else the value oracle's canonical hop set (ExpectedHopOffset, D-16) for a legacy label-carrying
        //      run — so the value-oracle facts keep computing completeness without a structural constant;
        //   3. else the union of observed stepIds across the whole cohort (framework-only fallback).
        var expectedByExec = expectedStepIdsByExecution
            ?? new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var orchestratorConsumed = orchestratorConsumedStepIdsByExecution
            ?? new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        var observedUnion = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in scored) observedUnion.UnionWith(r.DistinctStepIds);

        // The value oracle's canonical hop set: ExpectedHopOffset keys with a non-zero offset (offset 0 is the
        // Step_A seed/entry, excluded — the D-09 entry-marker analog on the label axis). Derived from the KEPT
        // ExpectedHopOffset (D-16), NOT a standalone structural hop-set constant (deleted per D-15).
        var valueOracleHopSet = ExpectedHopOffset.Where(kv => kv.Value != 0)
            .Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

        IReadOnlySet<string> ExpectedFor(RunTrace r)
        {
            var k = $"{r.CorrelationId}|{r.ExecutionId}";
            if (expectedByExec.TryGetValue(k, out var explicitSet)) return explicitSet;
            if (r.DistinctLabels.Count > 0) return valueOracleHopSet;   // legacy value-oracle completeness
            return observedUnion;                                       // framework-only fallback
        }

        // MISSING stepIds for a run: expected stepIds absent from the observed DistinctStepIds (ANL-02).
        List<string> MissingStepIds(RunTrace r)
        {
            var expected = ExpectedFor(r);
            return expected.Where(s => !r.DistinctStepIds.Contains(s)).ToList();
        }

        bool RunComplete(RunTrace r) => MissingStepIds(r).Count == 0;

        // COMPLETE (ANL-01): a scored run whose observed stepId set covers its expected set. Duplicate arrivals
        // (the convergent terminal fans in ×2) collapse in DistinctStepIds, exactly as the old label rule did.
        var complete = scored.Where(RunComplete).ToList();

        // ── B-CRITERION: classify started-but-incomplete runs by per-(corr,exec) RECOVERABILITY (75, D75-3) ──
        // MISSING (OBS-02) is the BINDING subset of started-but-incomplete runs: those that were RECOVERABLE-
        // but-lost. A started-but-incomplete run is TOLERATED (non-binding, cause-labeled) iff EITHER:
        //   (a) D75-3: the keeper logged a clean-absent DROP for its (corr,exec) — keeperOutcome[key] == "drop"
        //       — the L2 data was physically gone, so it was provably-unrecoverable by design; OR
        //   (b) D75-5: the redis-wipe timestamp path — its last hop precedes RECOVERY_UTC (stalled before the
        //       tier came back) AND it did not first appear after recovery (in-flight-at-wipe on TEST-05/07,
        //       which produce NO keeper drop log because a Redis exception routes to exhaustion, not a clean
        //       drop). Everything else recoverable-but-lost → binding miss. With recoveryUtc == null AND no
        //       keeper map, BOTH predicates are false so every incomplete run is a binding miss (unchanged).
        // WR-01 VETO (D75-5 upheld): a keeper "reinject" outcome for the key is HARD evidence the L2 data WAS
        //       recoverable and the keeper confirmed a re-send. That must ALWAYS override the redis-wipe timestamp
        //       heuristic — a reinjected-but-still-incomplete execution is recoverable-but-lost → BINDING FAIL,
        //       never tolerated by the (b) timestamp path. The veto only suppresses (b); it never affects the
        //       keeper-clean-DROP path (a) (drop and reinject are mutually exclusive outcomes for one key, and
        //       "reinject" wins any join tie — see AnalyzerE2ETests.BuildKeeperOutcomeMap).
        // D75-2: there is NO absolute in-flight-loss bound — the verdict never references a firing-rate proxy.
        // A fully-dead run (never started in ES) is invisible to either count.
        var firstHop = firstHopUtcByExecution ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var lastHop  = lastHopUtcByExecution  ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var keeperOutcome = keeperOutcomeByExecution ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var incomplete = scored.Where(r => !RunComplete(r)).ToList();
        var inFlightLoss = 0;
        var bindingMissing = 0;
        var telemetryGap = 0;
        var inFlightLossDetail = new List<string>();
        var unrecoverableLossDetail = new List<string>();
        var missingDetail = new List<string>();
        var telemetryGapDetail = new List<string>();

        foreach (var r in incomplete)
        {
            var key = $"{r.CorrelationId}|{r.ExecutionId}";
            var missingSteps = MissingStepIds(r);
            var missingList = string.Join(",", missingSteps.OrderBy(s => s, StringComparer.Ordinal));

            // ── FRAMEWORK-REDUNDANCY RECONCILIATION (ANL-03, NO seed oracle required) — SOLE non-binding path ──
            // (D-03/Phase 78: the value-oracle telemetry-gap path #1 is deleted; ANL-03 below is the ONLY
            // non-binding reconciliation. The sample author logs are gone post-Phase-77, so there is no seed /
            // surfaced-value evidence left to reconcile a dropped record — only framework redundancy remains.)
            // A missing expected stepId whose output EntryId (M_N) was consumed by an orchestrator FW-02
            // fan-out/terminal record PROVABLY ran — the orchestrator independently witnessed its output. When
            // EVERY missing stepId is so proven, the run's absent processor records are dropped telemetry, not
            // lost work: a NON-binding telemetry gap. This path needs NO value oracle (the ANL-03 acceptance) —
            // it is pure framework redundancy. A stepId missing from BOTH the processor records AND this proven
            // set drops through to the binding-miss classification below.
            var proven = orchestratorConsumed.TryGetValue(key, out var pset)
                ? pset : (IReadOnlySet<string>)EmptyStepSet;
            if (missingSteps.Count > 0 && missingSteps.All(proven.Contains))
            {
                telemetryGap++;
                telemetryGapDetail.Add(
                    $"[{key}] trace-incomplete (missing {missingList}) but every missing hop's output EntryId " +
                    "was consumed by an orchestrator fan-out/terminal record — framework-redundancy proven " +
                    "(dropped processor LOG, not lost work; telemetry gap, non-binding).");
                continue;
            }

            var startedAfterRecovery = recoveryUtc is { } rec
                && firstHop.TryGetValue(key, out var fh) && fh > rec;
            var stalledBeforeRecovery = recoveryUtc is { } rec2
                && lastHop.TryGetValue(key, out var lh) && lh < rec2;

            var keeperCleanDrop = keeperOutcome.TryGetValue(key, out var oc)
                && oc.Equals("drop", StringComparison.Ordinal);
            // WR-01: a confirmed keeper "reinject" (data WAS recoverable) VETOES the redis-wipe timestamp
            // tolerance — a reinjected-but-still-incomplete execution is recoverable-but-lost → binding miss.
            var keeperReinject = keeperOutcome.TryGetValue(key, out var oc2)
                && oc2.Equals("reinject", StringComparison.Ordinal);
            var redisWipeInFlight = stalledBeforeRecovery && !startedAfterRecovery && !keeperReinject;
            var tolerated = keeperCleanDrop || redisWipeInFlight;

            if (tolerated)
            {
                inFlightLoss++;
                if (keeperCleanDrop)
                {
                    var line = $"[{key}] keeper clean-absent DROP (provably-unrecoverable, tolerated).";
                    inFlightLossDetail.Add(line);
                    unrecoverableLossDetail.Add(line);
                }
                else
                {
                    inFlightLossDetail.Add(
                        $"[{key}] in-flight loss: last hop {lastHop[key]:o} < recovery {recoveryUtc:o} (tolerated).");
                }
            }
            else
            {
                bindingMissing++;
                missingDetail.Add(
                    $"[{key}] started-but-incomplete, recoverable-but-lost (no keeper clean-drop, not in-flight-at-wipe) → binding miss.");
            }
        }

        var missing = bindingMissing;

        // DUPLICATE (OBS-02, fail-closed, BINDING): any ILLEGITIMATE duplicate (correlationId, StepLabel) is a
        // FAIL. Reads the convergent-aware HasIllegitimateDuplicate (73, D-10), NOT the raw HasAnyDuplicateLabel,
        // so the legitimate Step_G ×2 fan-in does NOT trip the rule WHILE every other duplicate AND a Step_G
        // count ≠ 2 (1 = missing arrival, 3+ = same-entryId redelivery) still fail closed. No live dedupe counter
        // can corroborate a redelivery (dormant) → un-corroboratable → fail-closed.
        var duplicates = scored.Where(r => r.HasIllegitimateDuplicate).ToList();
        var dupFail = duplicates.Count > 0;

        // D-03/Phase 78: the concrete value-chain assertion (Values[label] == seed + hop-count) and the WR-02
        // vacuous-green guard are DELETED. Post-Phase-77 the sample author logs are gone, so no run surfaces a
        // Produced value — leaving the check dormant would be a vacuous-green trap. The verdict now stands on
        // structural stepId completeness + ANL-03 redundancy + the binding metric gate alone.

        var tripByExec = tripDurationMsByExecution ?? new Dictionary<string, double>(StringComparer.Ordinal);
        var tripByCorr = tripDurationMsByCorrelation ?? new Dictionary<string, double>(StringComparer.Ordinal);

        // ── METRIC GATE (binding, at quiescence) ──
        const double ConservationTol = 1.0;   // ±1 for window-boundary in-flight
        var conservationOk =
            Math.Abs(prom.OrchestratorMessagesConsumedAtEnd - prom.ProcessorMessagesSentAtEnd) <= ConservationTol;
        var keeperRecoveryOk = expectsKeeperActivity
            ? (prom.KeeperMessagesConsumedDelta > 0 && prom.KeeperMessagesSentDelta > 0)
            : true;   // scenarios recovered by broker redelivery don't require keeper activity
        var probeLiveOk = prom.KeeperL2ProbeRate > 0;

        var corroborationDetail = new List<string>();
        if (!conservationOk && mg1Binding)
            corroborationDetail.Add(
                $"MG-1 conservation FAIL: orchestrator_consumed@end={prom.OrchestratorMessagesConsumedAtEnd} != " +
                $"processor_sent@end={prom.ProcessorMessagesSentAtEnd} (>{ConservationTol}) — message loss or leak.");
        if (!keeperRecoveryOk)
            corroborationDetail.Add(
                $"MG-2 keeper-recovery FAIL: expected keeper activity but keeper_consumed={prom.KeeperMessagesConsumedDelta}, " +
                $"keeper_sent={prom.KeeperMessagesSentDelta} — recovery path did not run.");
        if (!probeLiveOk)
            corroborationDetail.Add($"MG-3 probe FAIL: keeper_l2_probe rate={prom.KeeperL2ProbeRate} (expected > 0).");

        var conservationContributes = !mg1Binding || conservationOk;   // reporting-only scenarios never fail on MG-1
        var metricGate = new MetricGateResult
        {
            ConservationOk = conservationOk,
            KeeperRecoveryOk = keeperRecoveryOk,
            ProbeLiveOk = probeLiveOk,
            ExpectsKeeperActivity = expectsKeeperActivity,
            Mg1Binding = mg1Binding,
        };
        var metricGateOk = conservationContributes && keeperRecoveryOk && probeLiveOk;

        // ── VERDICT (THREE-CLASS, split on EVIDENCE SUFFICIENCY first — Phase 76, ANL-04/05) ─────────
        // The verdict has three classes and splits on EVIDENCE SUFFICIENCY, not severity. FAIL requires
        // POSITIVE evidence of loss; ABSENCE of evidence is INCONCLUSIVE — never a false FAIL, never a
        // vacuous green.
        //
        // EVIDENCE-SUFFICIENCY GATE (checked FIRST, ANL-04a / ANL-05 blind): total trace darkness
        // (startedRuns == 0) AND self-consistent conservation (orchestrator_consumed == processor_sent within
        // ConservationTol, i.e. conservationOk) is the TEST-01 cold-ES / collector-blind shape. There are NO
        // runs to score, and if the metric gate fails it is because Prometheus scrapes an ABSENT collector
        // (probe/keeper read 0), NOT a real conservation gap — an observability-tier failure, not data loss.
        // So the verdict is INCONCLUSIVE regardless of metricGateOk (this is the ANL-05 metric-gate inversion:
        // a gate failing because the metrics tier is absent/frozen → INCONCLUSIVE, never FAIL). A GENUINE
        // conservation gap (conservationOk == false: live counters with orchestrator_consumed != processor_sent)
        // is positive evidence of loss, so it does NOT satisfy this gate and stays FAIL (ANL-05 live; the
        // T-76-12 guard — INCONCLUSIVE can never mask a real gap).
        //
        // When evidence IS sufficient, apply the existing ES-binding + binding-metric-gate PASS test. D-01 is
        // preserved: a reconciled run with a non-zero TelemetryGap is already excluded from `missing` above, so
        // it stays PASS — the non-binding gap never flips the verdict.
        var traceDark = startedRuns == 0;
        var evidenceInsufficient = traceDark && conservationOk;

        Verdict verdict;
        if (evidenceInsufficient)
        {
            verdict = Verdict.Inconclusive;
            // Blind-case reason carried in CorroborationDetail (and HumanSummary below) so an INCONCLUSIVE
            // report is trustworthy standalone — DISTINCT from a data-loss FAIL line.
            corroborationDetail.Add(
                "INCONCLUSIVE (evidence insufficient) — observability tier blind: total trace darkness " +
                $"(startedRuns=0) with self-consistent conservation (orchestrator_consumed@end=" +
                $"{prom.OrchestratorMessagesConsumedAtEnd} == processor_sent@end={prom.ProcessorMessagesSentAtEnd} " +
                $"within ±{ConservationTol}). An observability-degraded run — explicitly NOT a flow failure and " +
                "explicitly NOT a green; re-run deliberately against warm ES (no auto-retry).");
        }
        else
        {
            var pass = missing == 0 && !dupFail && metricGateOk;
            verdict = pass ? Verdict.Pass : Verdict.Fail;
        }

        var recon = corroborationDetail.Count == 0 ? ReconciliationOutcome.Reconciled : ReconciliationOutcome.Unreconciled;

        // Build the report (no IO).
        return new AnalyzerReport
        {
            ScenarioId = scenarioId,
            Verdict = verdict,
            StartedRuns = startedRuns,
            CompleteRuns = complete.Count,
            Missing = missing,
            MissingDetail = missingDetail,
            InFlightLoss = inFlightLoss,
            InFlightLossDetail = inFlightLossDetail,
            UnrecoverableLossDetail = unrecoverableLossDetail,
            Duplicates = duplicates,
            Reconciliation = recon,
            CorroborationDetail = corroborationDetail,
            Prom = prom,
            Traces = runs,
            TelemetryGap = telemetryGap,
            TelemetryGapDetail = telemetryGapDetail,
            TripDurationMsByExecution = tripByExec,
            TripDurationMsByCorrelation = tripByCorr,
            HumanSummary = BuildSummary(
                scenarioId, verdict, startedRuns, complete.Count, missing, dupFail,
                metricGateOk, recon, corroborationDetail, telemetryGap),
            MetricGate = metricGate,
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

    private static string BuildSummary(string scenarioId, Verdict verdict, int startedRuns,
        int completeRuns, int missing, bool dupFail, bool metricGateOk,
        ReconciliationOutcome recon,
        IReadOnlyList<string> corroborationDetail, int telemetryGap = 0)
    {
        var reasons = new List<string>();
        if (missing > 0) reasons.Add($"{missing} started-but-incomplete");
        if (dupFail) reasons.Add("illegitimate duplicate (fail-closed)");
        if (!metricGateOk) reasons.Add("metric gate (MG-1/2/3)");
        // Non-binding note: ANL-03 framework-redundancy-proven runs missing only hop-LOGS (dropped OTLP records).
        var gapNote = telemetryGap > 0 ? $" [{telemetryGap} telemetry-gap: dropped hop-logs, framework-redundancy-proven]" : "";

        var driver = verdict switch
        {
            Verdict.Pass => "every started run complete, no illegitimate duplicate, metric gate holds",
            // ANL-04/05: evidence insufficient — trace-dark + self-consistent conservation (collector-blind).
            Verdict.Inconclusive => "evidence insufficient — total trace darkness (startedRuns=0) with " +
                "self-consistent conservation; observability tier blind (explicitly not a flow failure, not a green)",
            _ => string.Join("; ", reasons),
        };

        // The metric gate is BINDING: an Unreconciled outcome is a FATAL gate failure, not a warning.
        var corroboration = recon == ReconciliationOutcome.Reconciled
            ? "metric gate clean"
            : $"metric gate [{corroborationDetail.Count} detail(s)]";

        return $"[{scenarioId}] {verdict}: {completeRuns}/{startedRuns} started runs complete — {driver}; {corroboration}.{gapNote}";
    }
}
