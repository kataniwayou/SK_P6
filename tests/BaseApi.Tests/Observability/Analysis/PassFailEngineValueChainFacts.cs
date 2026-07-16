using Xunit;

namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// Hermetic unit facts for the Phase-73 value-chain + convergent-terminal extension of
/// <see cref="PassFailEngine"/> (D-10/D-11/D-12). Proves the NEW binding logic ONCE, with synthetic
/// <see cref="RunTrace"/>s (built via the extended <see cref="RunTrace.FromLabels"/> carrying a per-label
/// <c>Values</c> map) + a synthetic <see cref="PromCounterSnapshot"/> — no live ES/Redis, sub-second. The
/// live RealStack fixture (Plan 04) builds traces through the SAME <see cref="RunTrace.FromLabels"/>, so a
/// green RealStack run is trustworthy rather than vacuous.
///
/// <para>
/// Behaviour matrix (73-SPEC R5/R6):
/// <list type="bullet">
/// <item><c>Pass</c> — 2 executions (seeds 100/200), all 10 labels, Step_G ×2 at seed+6, correct chain → Verdict.Pass.</item>
/// <item><c>Fail</c> (missing label) — drop Step_E1 → Missing &gt; 0 → Fail.</item>
/// <item><c>Fail</c> (Step_G count 1) — one G arrival → HasIllegitimateDuplicate → Fail.</item>
/// <item><c>Fail</c> (Step_G count 3 = same-entryId redelivery) — three G arrivals → Fail.</item>
/// <item><c>Fail</c> (wrong mid-chain value) — Values["Step_C"] = 999 → value-chain Fail.</item>
/// <item><c>Fail</c> (wrong terminal value) — Values["Step_G"] != seed+6 → value-chain (terminal-anchor proxy) Fail.</item>
/// <item><c>Fail</c> (non-convergent duplicate) — Step_C ×2 → Fail (fail-closed preserved).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Terminal-anchor (live proxy, D-11):</b> the Step_G map entry at <c>seed+6</c> (106 for exec_a/seed 100,
/// 206 for exec_b/seed 200) IS the live terminal-anchor proxy — the Step_G completed-terminal ES log carrying
/// seed+6. The auditor does NOT read a Redis <c>skp:out:</c> blob; that durable-blob proof is owned by the
/// hermetic harness (Plan 02 truth (d)). No live Redis read is performed or claimed here.
/// </para>
///
/// <para>
/// <b>Metrics are OUT OF SCOPE:</b> no metric-counter assertions are added; the synthetic Prom snapshot stays
/// corroboration-clean so the value-chain verdict is isolated and the Prom math never gates it.
/// </para>
/// </summary>
public sealed class PassFailEngineValueChainFacts
{
    /// <summary>The ten DISTINCT completeness labels (Step_G appears ×2 as RAW labels — see <see cref="RunFor"/>).</summary>
    private static readonly string[] NonTerminalLabels =
        { "Step_A", "Step_B", "Step_C", "Step_D1", "Step_E1", "Step_F1", "Step_D2", "Step_E2", "Step_F2" };

    /// <summary>
    /// The deterministic per-hop value chain for a given <paramref name="seed"/> (D-02): A=seed,
    /// B=seed+1, C=seed+2, {D1,D2}=seed+3, {E1,E2}=seed+4, {F1,F2}=seed+5, G=seed+6 (the shared terminal).
    /// </summary>
    private static Dictionary<string, int> ChainValues(int seed) => new(StringComparer.Ordinal)
    {
        ["Step_A"] = seed,
        ["Step_B"] = seed + 1,
        ["Step_C"] = seed + 2,
        ["Step_D1"] = seed + 3, ["Step_D2"] = seed + 3,
        ["Step_E1"] = seed + 4, ["Step_E2"] = seed + 4,
        ["Step_F1"] = seed + 5, ["Step_F2"] = seed + 5,
        ["Step_G"] = seed + 6,
    };

    /// <summary>
    /// Build a complete, correct-chain run for <paramref name="seed"/>: all 9 non-terminal labels ONCE +
    /// the convergent Step_G TWICE (the legitimate fan-in) as RAW labels, with the full chain value map.
    /// </summary>
    private static RunTrace RunFor(string corr, string exec, int seed)
    {
        var labels = NonTerminalLabels.Concat(new[] { "Step_G", "Step_G" }).ToArray();
        return RunTrace.FromLabels(corr, exec, labels, ChainValues(seed));
    }

    /// <summary>
    /// A clean snapshot that isolates the value-chain branch: result conservation HOLDS (consumed == sent == 0,
    /// |0−0| ≤ 1) and the keeper probe is LIVE (rate &gt; 0) so the Phase-74 binding metric gate passes and never
    /// confounds a value-chain verdict. Keeper recovery deltas stay 0 (no expectsKeeperActivity here → MG-2
    /// expects 0). Probe rate is positive so MG-3 passes — a zero probe would fail every Pass-asserting fact.
    /// </summary>
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

    // seeds locked by D-02: exec_a 100 (→ Step_G 106), exec_b 200 (→ Step_G 206).
    private const int SeedA = 100;
    private const int SeedB = 200;

    [Fact]
    public void Green_TwoExecutions_CorrectChain_StepGx2_AtTerminal_Yields_Pass()
    {
        // exec_a seed 100 (Step_G 106), exec_b seed 200 (Step_G 206); each all 10 labels, Step_G ×2, correct chain.
        var execA = RunFor("corr-1", "exec-a", SeedA);
        var execB = RunFor("corr-2", "exec-b", SeedB);
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { execA, execB }, snap, "unit-value-chain");

        Assert.Equal(Verdict.Pass, report.Verdict);
        Assert.True(report.ValueChainOk);
        Assert.Equal(2, report.CompleteRuns);
        // The legitimate Step_G ×2 fan-in did NOT trip the duplicate rule.
        Assert.False(execA.HasIllegitimateDuplicate);
        Assert.False(execB.HasIllegitimateDuplicate);
        Assert.Empty(report.Duplicates);
        // The Step_G map entry carries the terminal seed+6 — the LIVE terminal-anchor proxy (106 / 206).
        Assert.Equal(106, execA.Values["Step_G"]);
        Assert.Equal(206, execB.Values["Step_G"]);
    }

    [Fact]
    public void Fail_MissingLabel_DropStepE1_Yields_Fail()
    {
        // Drop Step_E1 from exec_a → 9 distinct labels → incomplete → Missing > 0 → Fail.
        var labels = NonTerminalLabels.Where(l => l != "Step_E1")
            .Concat(new[] { "Step_G", "Step_G" }).ToArray();
        var values = ChainValues(SeedA);
        values.Remove("Step_E1");
        var run = RunTrace.FromLabels("corr-1", "exec-a", labels, values);
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-value-chain");

        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.True(report.Missing > 0);
        Assert.NotEmpty(report.MissingDetail);
    }

    [Fact]
    public void Pass_MissingHopLogs_ValidTerminalChain_WithOracle_Reconciled_As_TelemetryGap()
    {
        // TRACE-EXPORT HARDENING (Layer B) — the TEST-10 live case. Under load the OTLP pipeline can DROP an
        // intermediate hop's LOG even though the hop RAN. Here Step_E1 AND Step_E2 logs are absent, but every
        // surfaced value forms a valid chain culminating in Step_G == seed+6 (206) — unreachable unless E
        // actually ran (F=205 ⇒ E produced 204). With a value ORACLE supplied (the live fixture always does),
        // the run is reconciled as a NON-binding telemetry gap, NOT a recoverable-but-lost miss.
        var labels = NonTerminalLabels.Where(l => l != "Step_E1" && l != "Step_E2")
            .Concat(new[] { "Step_G", "Step_G" }).ToArray();
        var values = ChainValues(SeedB);
        values.Remove("Step_E1");
        values.Remove("Step_E2");
        var run = RunTrace.FromLabels("corr-1", "exec-b", labels, values);
        var snap = CleanSnapshot();
        var seedOracle = new Dictionary<string, int>(StringComparer.Ordinal) { ["corr-1|exec-b"] = SeedB };

        var report = new PassFailEngine().Analyze(
            new[] { run }, snap, "unit-value-chain", seedsByExecution: seedOracle);

        Assert.Equal(0, report.Missing);              // NOT a recoverable-but-lost binding miss
        Assert.Equal(1, report.TelemetryGap);         // reclassified as a dropped-log telemetry gap
        Assert.True(report.ValueChainOk);             // surfaced chain intact (E not surfaced → not checked)
        Assert.Equal(Verdict.Pass, report.Verdict);   // work provably completed (terminal 206)
        Assert.Contains(report.TelemetryGapDetail, d => d.Contains("Step_E1") && d.Contains("Step_E2"));
    }

    [Fact]
    public void Fail_MissingHopLog_WrongTerminal_WithOracle_StillBindingMiss()
    {
        // GUARD: the reconciliation must NEVER rescue a run whose terminal is WRONG (or absent) — that is a real
        // loss, not a dropped log. Missing Step_E1 AND a tampered Step_G (205 != seed+6=206) ⇒ the terminal
        // anchor check fails ⇒ NOT reconciled ⇒ still a binding miss (and value-chain fail) ⇒ Fail.
        var labels = NonTerminalLabels.Where(l => l != "Step_E1")
            .Concat(new[] { "Step_G", "Step_G" }).ToArray();
        var values = ChainValues(SeedB);
        values.Remove("Step_E1");
        values["Step_G"] = 205; // wrong terminal — should be 206
        var run = RunTrace.FromLabels("corr-1", "exec-b", labels, values);
        var snap = CleanSnapshot();
        var seedOracle = new Dictionary<string, int>(StringComparer.Ordinal) { ["corr-1|exec-b"] = SeedB };

        var report = new PassFailEngine().Analyze(
            new[] { run }, snap, "unit-value-chain", seedsByExecution: seedOracle);

        Assert.Equal(0, report.TelemetryGap);         // NOT reconciled (terminal wrong)
        Assert.True(report.Missing > 0);              // genuine binding miss
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public void Fail_StepGCountOne_MissingArrival_Yields_Fail()
    {
        // Step_G appears ONCE (one arrival) → HasIllegitimateDuplicate (count != 2) → Fail.
        var labels = NonTerminalLabels.Concat(new[] { "Step_G" }).ToArray();
        var run = RunTrace.FromLabels("corr-1", "exec-a", labels, ChainValues(SeedA));
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-value-chain");

        Assert.True(run.HasIllegitimateDuplicate);
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEmpty(report.Duplicates);
    }

    [Fact]
    public void Fail_StepGCountThree_SameEntryIdRedelivery_Yields_Fail()
    {
        // Step_G appears THREE times (same-entryId redelivery) → HasIllegitimateDuplicate (count != 2) → Fail.
        var labels = NonTerminalLabels.Concat(new[] { "Step_G", "Step_G", "Step_G" }).ToArray();
        var run = RunTrace.FromLabels("corr-1", "exec-a", labels, ChainValues(SeedA));
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-value-chain");

        Assert.True(run.HasIllegitimateDuplicate);
        Assert.Contains("Step_G", run.DuplicateLabels);
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public void Fail_WrongMidChainValue_StepC_Yields_Fail()
    {
        // A complete run with Step_G ×2 and the right multiplicity, but Step_C carries a tampered value
        // (999 != seed+2) → value-chain fail → Fail. Catches a mid-hop L2 mutation (T-73-05).
        var values = ChainValues(SeedA);
        values["Step_C"] = 999;
        var labels = NonTerminalLabels.Concat(new[] { "Step_G", "Step_G" }).ToArray();
        var run = RunTrace.FromLabels("corr-1", "exec-a", labels, values);
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-value-chain");

        Assert.False(report.ValueChainOk);
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEmpty(report.ValueChainDetail);
        Assert.Contains(report.ValueChainDetail, d => d.Contains("Step_C") && d.Contains("999"));
    }

    [Fact]
    public void Fail_WrongTerminalValue_StepG_Yields_Fail()
    {
        // Step_G at the WRONG terminal value (105 != seed+6=106) → value-chain (terminal-anchor proxy) fail
        // → Fail. This is the live terminal-anchor proxy: a wrong Step_G completed-terminal value fails closed.
        var values = ChainValues(SeedA);
        values["Step_G"] = 105; // should be 106
        var labels = NonTerminalLabels.Concat(new[] { "Step_G", "Step_G" }).ToArray();
        var run = RunTrace.FromLabels("corr-1", "exec-a", labels, values);
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-value-chain");

        Assert.False(report.ValueChainOk);
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.Contains(report.ValueChainDetail, d => d.Contains("Step_G") && d.Contains("106"));
    }

    [Fact]
    public void Fail_NonConvergentDuplicate_StepC_x2_Yields_Fail()
    {
        // Step_C appears TWICE (a NON-convergent duplicate) alongside the legitimate Step_G ×2 →
        // HasIllegitimateDuplicate → Fail (the fail-closed rule is preserved for every non-convergent label).
        var labels = NonTerminalLabels.Concat(new[] { "Step_C", "Step_G", "Step_G" }).ToArray();
        var run = RunTrace.FromLabels("corr-1", "exec-a", labels, ChainValues(SeedA));
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-value-chain");

        Assert.True(run.HasIllegitimateDuplicate);
        Assert.Contains("Step_C", run.DuplicateLabels);
        Assert.DoesNotContain("Step_G", run.DuplicateLabels); // the convergent fan-in stays legitimate
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public void Fail_CompleteRun_ZeroSurfacedValues_WithValueOracle_Yields_Fail()
    {
        // WR-02: a COMPLETE run (all 10 distinct labels, Step_G ×2 legitimate) that surfaced ZERO Produced
        // values — e.g. the live ES mapping for attributes.Produced is absent/odd-shaped so every
        // TryReadProduced returned false, collapsing the value map to empty. PREVIOUSLY this run was silently
        // skipped and valueChainOk stayed true (a vacuous green). With a value ORACLE supplied
        // (seedsByExecution non-null — exactly what the live fixture always passes), the binding gate must now
        // treat the unchecked terminal cohort as UNVERIFIED → Fail.
        var labels = NonTerminalLabels.Concat(new[] { "Step_G", "Step_G" }).ToArray();
        var emptyValues = new Dictionary<string, int>(StringComparer.Ordinal); // ZERO surfaced Produced values
        var run = RunTrace.FromLabels("corr-1", "exec-a", labels, emptyValues);
        var snap = CleanSnapshot();

        // The value oracle is SUPPLIED (non-null) — this is what gates the new rule. The legacy
        // completeness-only callers (PassFailEngineFacts) pass null here and stay unaffected.
        var seedOracle = new Dictionary<string, int>(StringComparer.Ordinal) { ["corr-1|exec-a"] = SeedA };

        var report = new PassFailEngine().Analyze(
            new[] { run }, snap, "unit-value-chain",
            seedsByExecution: seedOracle);

        Assert.Equal(0, report.Missing);          // completeness alone is satisfied (all 10 labels present)
        Assert.False(report.ValueChainOk);        // …but the value chain is UNVERIFIED (no surfaced values)
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.Contains(report.ValueChainDetail, d => d.Contains("no Produced value") && d.Contains("unverified"));
    }

    [Fact]
    public void Pass_CompleteRun_ZeroSurfacedValues_WithoutValueOracle_StaysGreen()
    {
        // Legacy/completeness-only path: NO value oracle supplied (seedsByExecution null) AND no surfaced
        // values. The WR-02 hardening MUST NOT fire here — the run is genuinely not value-chain-checked, exactly
        // as the migrated PassFailEngineFacts rely on. completeness + duplicate alone gate the verdict.
        var labels = NonTerminalLabels.Concat(new[] { "Step_G", "Step_G" }).ToArray();
        var run = RunTrace.FromLabels("corr-1", "exec-a", labels); // no values, no oracle
        var snap = CleanSnapshot();

        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-value-chain");

        Assert.True(report.ValueChainOk);          // not value-chain-checked (no oracle) → stays true
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void ToleratedInFlightLoss_PartialTerminalOnly_NotValueChainFailed_Yields_Pass()
    {
        // TEST-03 regression (orchestrator crash): a run stalled mid-flight DURING the outage — only the
        // terminal Step_G survived in ES (the earlier hops were lost), so its last hop precedes RECOVERY_UTC
        // and it is classified as a TOLERATED in-flight loss (counted in InFlightLoss, NOT Missing). Its
        // partial trace carries only Step_G at the real terminal value (206) with no Step_B/Step_A to anchor
        // the seed, so the binding value-chain check — IF it ran on this run — would recover seed 0 and
        // FALSELY fail (Step_G expected 6 got 206). A run already classified as a tolerated in-flight loss
        // MUST NOT also trip the binding value chain; only its (bounded) loss count gates the verdict.
        var recovery = DateTimeOffset.Parse("2026-07-15T06:21:33Z");

        // One complete, correct run (so the window isn't vacuous) plus the stalled partial (Step_G ×2 @ 206).
        var good = RunFor("corr-good", "exec-good", SeedB); // seed 200, full chain, Step_G 206
        var partial = RunTrace.FromLabels("corr-loss", "exec-loss", new[] { "Step_G", "Step_G" },
            new Dictionary<string, int>(StringComparer.Ordinal) { ["Step_G"] = 206 });
        var snap = CleanSnapshot();

        var lastHop = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["corr-good|exec-good"] = recovery.AddSeconds(30),  // finished AFTER recovery (not a loss)
            ["corr-loss|exec-loss"] = recovery.AddSeconds(-60), // stalled BEFORE recovery → tolerated in-flight loss
        };

        var report = new PassFailEngine().Analyze(
            new[] { good, partial }, snap, "unit-inflight-valuechain",
            recoveryUtc: recovery,
            lastHopUtcByExecution: lastHop);

        Assert.Equal(1, report.InFlightLoss); // the partial is a tolerated in-flight loss
        Assert.Equal(0, report.Missing);      // …NOT a binding miss
        Assert.True(report.ValueChainOk);     // …and NOT value-chain-checked (its partial chain is expected)
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void Green_Report_Carries_TripDurationMaps_And_ValueChainDetail_Present()
    {
        // The report threads the (synthetic) trip-duration maps through Analyze and surfaces value-chain
        // evidence — proving the new AnalyzerReport fields are wired even on a green run.
        var execA = RunFor("corr-1", "exec-a", SeedA);
        var execB = RunFor("corr-2", "exec-b", SeedB);
        var snap = CleanSnapshot();

        var tripByExec = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["corr-1|exec-a"] = 1234.0,
            ["corr-2|exec-b"] = 2345.0,
        };
        var tripByCorr = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["corr-1"] = 1234.0,
            ["corr-2"] = 2345.0,
        };

        var report = new PassFailEngine().Analyze(
            new[] { execA, execB }, snap, "unit-value-chain",
            tripDurationMsByExecution: tripByExec, tripDurationMsByCorrelation: tripByCorr);

        Assert.Equal(Verdict.Pass, report.Verdict);
        Assert.Equal(1234.0, report.TripDurationMsByExecution["corr-1|exec-a"]);
        Assert.Equal(2345.0, report.TripDurationMsByCorrelation["corr-2"]);
        // ValueChainDetail is populated (one OK line per checked run) even on a green run.
        Assert.Equal(2, report.ValueChainDetail.Count);
    }
}
