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
/// <item>METRIC GATE (inert): the former Prom corroboration math (impliedRuns = round(sent/9),
///   spawn-aware result reconciliation) was retired as miscalibrated; the gate is inert today and a
///   later task repurposes it as a metric gate. The Reconciliation/CorroborationDetail report fields
///   are kept for that future use.</item>
/// <item>VERDICT: Pass iff (every started run complete) AND (no duplicate) AND (value-chain intact).</item>
/// </list>
/// </para>
/// </summary>
public sealed class PassFailEngine
{
    /// <summary>
    /// The 9 per-execution HOP labels (B→C→{D1→E1→F1, D2→E2→F2}→G), with the single shared convergent
    /// terminal Step_G reachable from BOTH sinks (73, D-10). A COMPLETE per-(corr,exec) run's DISTINCT hop
    /// set equals this exactly — Step_G logs TWICE per run (the per-arrival fan-in) but DISTINCT collapses it
    /// to one.
    /// <para>
    /// Step_A is DELIBERATELY excluded: it is the SHARED Mode-2 entry/seed, logged ONCE per correlationId
    /// with executionId=Guid.Empty (no per-execution log, no Produced value), so it is filtered out of the
    /// per-execution Step_* cohort and NEVER appears in a per-(corr,exec) trace — verified LIVE (phase-73
    /// harness: every Step_A hit surfaces with empty ExecutionId, while Step_B..Step_G carry minted
    /// executionIds + Produced values). Step_A's correctness is proven via the value chain's seed (see
    /// <see cref="ResolveSeed"/> / <see cref="PerHopOffset"/>, which already treat Step_A as the optional
    /// offset-0 seed source). This REFINES D-10's "10-label DAG" to the per-execution completeness of its 9
    /// observable hops; <see cref="IsComplete"/> tolerates Step_A's presence (synthetic facts pass it) or
    /// absence (the live cohort) so the live and hermetic paths share one rule.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> HopLabels = new(StringComparer.Ordinal)
    { "Step_B", "Step_C", "Step_D1", "Step_E1", "Step_F1", "Step_D2", "Step_E2", "Step_F2", "Step_G" };

    /// <summary>
    /// A run is COMPLETE when its distinct labels — IGNORING the optional shared <c>Step_A</c> entry — are
    /// exactly the 9 per-execution <see cref="HopLabels"/>. Stripping Step_A before the equality preserves the
    /// strict "no missing hop, no unexpected label" check while tolerating Step_A being present (synthetic
    /// facts include it) or absent (the live per-execution cohort never carries it).
    /// </summary>
    private static bool IsComplete(RunTrace r)
        => r.DistinctLabels.Where(l => !l.Equals("Step_A", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal).SetEquals(HopLabels);

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
    /// <param name="seedsByExecution">
    /// VALUE-CHAIN seed map (73, D-11): per-<c>(corr, exec)</c> seed keyed <c>"correlationId|executionId"</c>, used to
    /// check each label's surfaced value equals <c>seed + hop-count</c>. When a run has no entry here, its seed is
    /// recovered from the chain itself (<c>Values["Step_B"] - 1</c>). Default null ⇒ recover-from-chain for all.
    /// </param>
    public AnalyzerReport Analyze(IReadOnlyList<RunTrace> runs, PromCounterSnapshot prom,
                                  string scenarioId,
                                  IReadOnlyDictionary<string, double>? tripDurationMsByExecution = null,
                                  IReadOnlyDictionary<string, double>? tripDurationMsByCorrelation = null,
                                  IReadOnlyDictionary<string, int>? seedsByExecution = null,
                                  bool expectsKeeperActivity = false)
    {
        // ── ES-BINDING ARBITER (67-03) ────────────────────────────────────────────────────────────

        // STARTED (denominator): distinct (correlationId, executionId) instances with ≥1 Step_* log = one
        // RunTrace each (each spawned execution is its own run).
        var startedRuns = runs.Count;

        // COMPLETE (OBS-01): the 9 per-execution hops (both sinks + the convergent terminal Step_G) all
        // present, the shared Step_A entry ignored (IsComplete). Step_G logs ×2 per run but DISTINCT
        // collapses it to one (73, D-10).
        var complete = runs.Where(IsComplete).ToList();

        // MISSING (OBS-02): started-but-incomplete. Bound against the ES STARTED denominator — NOT the
        // Prom dispatch count. A fully-dead run (never started in ES) is invisible here and is not
        // detected by the current engine (the Prom dead-run corroboration warning was retired).
        var missing = startedRuns - complete.Count;
        var missingDetail = new List<string>();
        if (missing > 0)
        {
            missingDetail.Add(
                $"{missing} of {startedRuns} STARTED run(s) (distinct (correlationId, executionId) with ≥1 Step_* log) " +
                "did NOT reach COMPLETE (all 9 per-execution hops incl. both sinks Step_F1 + Step_F2 and the convergent " +
                "terminal Step_G; the shared Step_A entry/seed is verified via the value chain, not the hop set).");
            missingDetail.Add(
                "A fully-dead run (dispatched but never logging any Step_*) never started in ES and is NOT in this count. " +
                "The specific missing correlationId for such a run is NOT recoverable from telemetry (research item #1).");
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
        // sit at the shared terminal seed+6.
        //
        // WR-01 — what this pins on the LIVE path: the seed is recovered from the chain itself (Step_B - 1, see
        // ResolveSeed) because the ES-read-only auditor has no independent absolute seed oracle live. So the LIVE
        // check binds the inter-hop +1 DELTAS (and the Step_G ×2 agreement at the shared terminal seed+6), NOT
        // the ABSOLUTE base value — a uniform constant shift of the whole live chain would still pass. The
        // ABSOLUTE-value terminal-anchor proof (Step_G at 106 / 206 against the fixed 100/200 seeds) is owned by
        // the hermetic harness (FanInHermeticHarnessFacts, Plan 02), which reads the durable L2 blob; it is NOT
        // re-proven here. The hermetic value-chain facts pass an EXPLICIT seed oracle, so their anchor is real.
        // A run is checked ONLY if it surfaced any values; WR-02 below additionally fails a COMPLETE-but-zero-
        // values cohort when a value oracle is supplied. valueChainOk folds into the verdict.
        var valueChainDetail = new List<string>();
        var valueChainOk = true;

        // VALUE-ORACLE GATE (WR-02): the empty-value-map vacuous-pass hardening below only applies when the
        // caller actually supplied a value oracle (a non-null seedsByExecution). The migrated legacy
        // completeness-only callers (PassFailEngineFacts) pass NO value map AND NO seedsByExecution — for them
        // the empty-map run is genuinely not value-chain-checked and the run continues as before (no behaviour
        // change). When an oracle IS supplied (the live fixture, the value-chain facts), a COMPLETE run that
        // surfaced ZERO Produced values is treated as UNVERIFIED (fail), NOT a vacuous pass.
        var valueOracleSupplied = seedsByExecution is not null;
        var completeRunsWithValues = 0;

        foreach (var run in runs)
        {
            if (run.Values.Count == 0)
            {
                continue; // no surfaced values → checked for vacuous-pass below when a value oracle is supplied
            }

            if (IsComplete(run))
            {
                completeRunsWithValues++;
            }

            var seed = ResolveSeed(run, seedsByExecution);
            if (!CheckValueChain(run, seed, out var detail))
            {
                valueChainOk = false;
            }
            valueChainDetail.Add(detail);
        }

        // WR-02 vacuous-green guard: with a value oracle supplied, if there is at least one COMPLETE run but
        // NONE of them surfaced a Produced value, the binding value-chain assertion checked NOTHING for the
        // terminal cohort — report it UNVERIFIED (fail) rather than a silent OK. The most likely live cause is
        // the ES mapping for attributes.Produced being absent/odd-shaped so TryReadProduced returns false for
        // every hit, collapsing every run's value map to empty.
        if (valueOracleSupplied && complete.Count > 0 && completeRunsWithValues == 0)
        {
            valueChainOk = false;
            valueChainDetail.Add(
                $"{complete.Count} complete run(s) surfaced no Produced value — value chain unverified " +
                "(value oracle supplied but no terminal cohort carried a surfaced value; likely attributes.Produced unmapped).");
        }

        var tripByExec = tripDurationMsByExecution ?? new Dictionary<string, double>(StringComparer.Ordinal);
        var tripByCorr = tripDurationMsByCorrelation ?? new Dictionary<string, double>(StringComparer.Ordinal);

        // ── METRIC GATE (binding, at quiescence) ──
        const double ConservationTol = 1.0;   // ±1 for window-boundary in-flight
        var conservationOk =
            Math.Abs(prom.OrchestratorMessagesConsumedDelta - prom.ProcessorMessagesSentDelta) <= ConservationTol;
        var keeperRecoveryOk = expectsKeeperActivity
            ? (prom.KeeperMessagesConsumedDelta > 0 && prom.KeeperMessagesSentDelta > 0)
            : true;   // scenarios recovered by broker redelivery don't require keeper activity
        var probeLiveOk = prom.KeeperL2ProbeRate > 0;

        var corroborationDetail = new List<string>();
        if (!conservationOk)
            corroborationDetail.Add(
                $"MG-1 conservation FAIL: orchestrator_consumed={prom.OrchestratorMessagesConsumedDelta} != " +
                $"processor_sent={prom.ProcessorMessagesSentDelta} (>{ConservationTol}) — message loss or leak.");
        if (!keeperRecoveryOk)
            corroborationDetail.Add(
                $"MG-2 keeper-recovery FAIL: expected keeper activity but keeper_consumed={prom.KeeperMessagesConsumedDelta}, " +
                $"keeper_sent={prom.KeeperMessagesSentDelta} — recovery path did not run.");
        if (!probeLiveOk)
            corroborationDetail.Add($"MG-3 probe FAIL: keeper_l2_probe rate={prom.KeeperL2ProbeRate} (expected > 0).");

        var metricGate = new MetricGateResult
        {
            ConservationOk = conservationOk,
            KeeperRecoveryOk = keeperRecoveryOk,
            ProbeLiveOk = probeLiveOk,
            ExpectsKeeperActivity = expectsKeeperActivity,
        };
        var metricGateOk = conservationOk && keeperRecoveryOk && probeLiveOk;
        var recon = corroborationDetail.Count == 0 ? ReconciliationOutcome.Reconciled : ReconciliationOutcome.Unreconciled;

        // ── VERDICT (ES-binding + binding metric gate) ─────────────────────────────────────────────
        // The value-chain check (incl. the Step_G-at-seed+6 terminal-anchor proxy) is BINDING (73, D-11),
        // and the metric gate (MG-1/2/3, metricGateOk) is now ALSO binding — a failing gate flips a green
        // ES verdict to Fail and sets Reconciliation=Unreconciled (the legacy round(sent/9) corroboration
        // that was non-fatal is retired).
        var pass = missing == 0 && !dupFail && valueChainOk && metricGateOk;
        var verdict = pass ? Verdict.Pass : Verdict.Fail;

        // Build the report (no IO).
        return new AnalyzerReport
        {
            ScenarioId = scenarioId,
            Verdict = verdict,
            StartedRuns = startedRuns,
            CompleteRuns = complete.Count,
            Missing = missing,
            MissingDetail = missingDetail,
            Duplicates = duplicates,
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

    /// <summary>
    /// Resolve the seed for a run's value chain (73, D-11): the explicit <paramref name="seedsByExecution"/>
    /// entry keyed <c>"correlationId|executionId"</c> if present, else recovered from the chain itself
    /// (<c>Values["Step_B"] - 1</c>), else <c>Values["Step_A"]</c> (the seed source), else 0.
    /// <para>
    /// WR-01 — anchor strength. On the LIVE path the seed is recovered from the chain (<c>Step_B - 1</c>),
    /// because no independent live executionId→seed oracle exists (the framework-owned GUID executionId carries
    /// no seed, and Mode-2 <c>Step_A</c> surfaces no <c>Produced</c> value). A chain-derived seed makes
    /// <c>Step_B</c>'s own assertion a tautology and pins only the inter-hop <c>+1</c> DELTAS, NOT the ABSOLUTE
    /// base — a uniform constant shift of the whole chain still passes. The absolute-value proof (101..106 /
    /// 201..206) is owned by the hermetic harness (<c>FanInHermeticHarnessFacts</c>), which reads the durable L2
    /// blob. The hermetic value-chain facts DO pass an explicit seed oracle, so their absolute anchor is real.
    /// </para>
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
