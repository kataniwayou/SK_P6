# Resilience Suite Metric-Binding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the 7-scenario live resilience suite's verdict trustworthy and honest — fix the phase-74 metric miscalibration, bind the proof on business-metric invariants, and make L2-wipe scenarios deterministic — touching `tests/` and `scripts/` only.

**Architecture:** `PassFailEngine` is a pure function `(RunTrace[], PromCounterSnapshot, timestamps) → AnalyzerReport`; the RealStack fixture (`AnalyzerE2ETests`) feeds it parsed ES + Prom inputs, and the PowerShell harness (`phase-67-harness.ps1`) drives the live stack. We delete the miscalibrated Prom corroboration, add keeper deltas + three binding metric gates evaluated at quiescence, and add recovery-timestamp-based in-flight loss classification. All new engine branches are proven hermetically with synthetic inputs (the existing `PassFailEngineFacts` pattern).

**Tech Stack:** C# / xUnit.v3 under Microsoft.Testing.Platform; PowerShell 7 ops scripts; Prometheus HTTP API; Elasticsearch `_search`. .NET 8.

---

## Important conventions (read before starting)

- **Run hermetic tests** by the EXE directly (the `dotnet test` MTP wrapper hangs on Windows):
  `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-class "*PassFailEngineFacts*" --timeout 4m`
  (`--filter-not-trait "Category=RealStack"` for the whole hermetic suite.)
- **Live scenarios** run via `pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-0X` (single) or the sweep with a proper array: `pwsh -Command "& './scripts/phase-68-sweep.ps1' -ScenarioIds 'TEST-02','TEST-03'"`.
- **`src/` is OUT OF BOUNDS.** No file under `src/` is modified by this plan. The drift-guard reads `src/` but never edits it.
- The single source of truth for live metric names will be a new test-project static `LiveMetricNames` — analyzer and drift-guard both reference it.

## File structure

| File | Responsibility | Phase |
|------|----------------|-------|
| `tests/BaseApi.Tests/Observability/LiveMetricNames.cs` | **new** — one const per live metric series name (single source of truth) | 1 |
| `tests/BaseApi.Tests/Observability/HarnessMetricDriftFacts.cs` | **new** — assert the harness script + analyzer agree on metric names | 1 |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` | retire corroboration; add metric gate + in-flight classification | 1,2,3 |
| `tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs` | add keeper deltas + probe rate | 2 |
| `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` | drop retired fields; add metric-gate + in-flight-loss fields | 1,2,3 |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` | delete obsolete corroboration facts; add metric-gate + classification facts | 1,2,3 |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` | use `LiveMetricNames`; read keeper deltas; drain; pass recovery/last-hop | 1,2,3 |
| `scripts/phase-67-harness.ps1` | drain-to-quiescence; export `RECOVERY_UTC` | 2,3 |

---

# PHASE 1 — Trust the existing suite

Outcome: a clean sweep no longer shows spurious `Unreconciled`; a drift-guard test fails if the harness/analyzer metric names diverge again.

## Task 1.1: Single source of truth for live metric names

**Files:**
- Create: `tests/BaseApi.Tests/Observability/LiveMetricNames.cs`

- [ ] **Step 1: Create the constants file**

```csharp
namespace BaseApi.Tests.Observability;

/// <summary>
/// Single source of truth for the Prometheus series names the live-stack analyzer and the ops
/// harness query. Phase 74 renamed the orchestrator dispatch counter and updated the analyzer but
/// NOT the harness, which silently aborted the whole fault suite. Centralising the names here lets
/// HarnessMetricDriftFacts assert the harness script and the analyzer agree. The collector appends
/// the Prometheus `_total` suffix, so these carry it (the src instrument names do not).
/// </summary>
public static class LiveMetricNames
{
    public const string OrchestratorMessagesSentTotal = "orchestrator_messages_sent_total";
    public const string OrchestratorMessagesConsumedTotal = "orchestrator_messages_consumed_total";
    public const string ProcessorMessagesConsumedTotal = "processor_messages_consumed_total";
    public const string ProcessorMessagesSentTotal = "processor_messages_sent_total";
    public const string KeeperMessagesConsumedTotal = "keeper_messages_consumed_total";
    public const string KeeperMessagesSentTotal = "keeper_messages_sent_total";
    public const string KeeperL2ProbeTotal = "keeper_l2_probe_total";
}
```

- [ ] **Step 2: Commit**

```bash
git add tests/BaseApi.Tests/Observability/LiveMetricNames.cs
git commit -m "test(analyzer): add LiveMetricNames single source of truth for metric series"
```

## Task 1.2: Metric-name drift guard

**Files:**
- Create: `tests/BaseApi.Tests/Observability/HarnessMetricDriftFacts.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Drift guard (phase-74 post-mortem): the orchestrator fire-count metric name is hard-coded in the
/// PowerShell harness (Get-FireCount) AND consumed by the analyzer. A rename that updates one but not
/// the other silently aborts the fault suite. This fact fails at build time if the harness script no
/// longer references the LiveMetricNames token the analyzer expects.
/// </summary>
public sealed class HarnessMetricDriftFacts
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "scripts", "phase-67-harness.ps1")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.True(dir is not null, "Could not locate repo root (scripts/phase-67-harness.ps1) above the test bin.");
        return dir!;
    }

    [Fact]
    public void Harness_FireGate_References_The_Orchestrator_Sent_Metric_Name()
    {
        var harness = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "phase-67-harness.ps1"));
        Assert.Contains(LiveMetricNames.OrchestratorMessagesSentTotal, harness);
    }

    [Fact]
    public void Analyzer_Fixture_References_The_Orchestrator_Sent_Metric_Name()
    {
        var analyzer = File.ReadAllText(Path.Combine(
            RepoRoot(), "tests", "BaseApi.Tests", "Observability", "AnalyzerE2ETests.cs"));
        Assert.Contains(LiveMetricNames.OrchestratorMessagesSentTotal, analyzer);
    }
}
```

- [ ] **Step 2: Run to verify it passes already** (the fix in `1f3ad75` put the name in the harness; the analyzer already references it)

Run: `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-class "*HarnessMetricDriftFacts*" --timeout 2m`
Expected: PASS, 2 succeeded. *(This guard would have FAILED before `1f3ad75`, which is the point.)*

- [ ] **Step 3: Prove the guard bites** — temporarily edit `scripts/phase-67-harness.ps1` line 234, change `orchestrator_messages_sent_total` to `orchestrator_messages_sent_total_BROKEN`, re-run the filter above, confirm `Harness_FireGate_…` FAILS, then revert the edit and confirm PASS again.

- [ ] **Step 4: Commit**

```bash
git add tests/BaseApi.Tests/Observability/HarnessMetricDriftFacts.cs
git commit -m "test(harness): add metric-name drift guard (harness <-> analyzer)"
```

## Task 1.3: Retire the miscalibrated Prom corroboration

The `impliedRuns = round(messages_sent / 9)` and `consumed ≈ sent + spawnExtra` math assumed the old per-step counter; the new `orchestrator_messages_sent` is ~18–21/run, so both raise spurious warnings. Delete them (verdict is already ES-binding). The `Reconciliation`/`CorroborationDetail` report fields are **kept** and repurposed by the Phase-2 metric gate.

**Files:**
- Modify: `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`
- Modify: `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs`
- Modify: `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs`
- Modify: `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`

- [ ] **Step 1: Delete the obsolete corroboration facts.** In `PassFailEngineFacts.cs` remove these four facts entirely (they assert the deleted math): `PromDeadRun_ImpliesMoreRunsThanEs_Yields_NonFatalWarning`, `PromWindowEdge_OneRunMismatch_WithinTolerance_StaysClean`, `SpawnAware_ResultExceedsDispatchByExactlySpawnExtra_StaysClean`, `SpawnAware_ResultMismatch_RaisesNonFatalWarning`. Also delete the `CorroboratingSnapshot` and `TriggerCountOf` helpers' bodies that reference `PassFailEngine.LabelsPerRun` — replace `CorroboratingSnapshot` with the clean-snapshot helper below (keeper fields land in Phase 2).

```csharp
// Replaces CorroboratingSnapshot — a clean 4-delta snapshot; no per-run scaling (corroboration retired).
private static PromCounterSnapshot CleanSnapshot() => new()
{
    OrchestratorMessagesSentDelta = 0,
    OrchestratorMessagesConsumedDelta = 0,
    ProcessorMessagesConsumedDelta = 0,
    ProcessorMessagesSentDelta = 0,
};
```

Update the three surviving facts (`Complete_…`, `Incomplete_…`, `Duplicate_…`, `RetiredConflation_…`, `RemovedDedupCounters_…`) to call `CleanSnapshot()` and the simplified `Analyze` (the call drops `TriggerCountOf(snap)` — see Step 3 signature). Remove the `Assert.Equal(ReconciliationOutcome.Reconciled, …)` line from `Complete_…` (Reconciliation is repurposed in Phase 2; for now it defaults to `Reconciled`).

- [ ] **Step 2: Run to verify the facts file fails to compile** (it references deleted symbols)

Run: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo`
Expected: compile errors in `PassFailEngineFacts.cs` referencing `LabelsPerRun` / `TriggerCountFrom` / the removed `Analyze` params — this confirms the call sites we must update.

- [ ] **Step 3: Strip the corroboration from `PassFailEngine.cs`.** Remove: the `LabelsPerRun`, `CorroborationRunTolerance` consts; the `TriggerCountFrom` method; the `triggerCount` and `spawnExtra` parameters from `Analyze`; the entire "PROM CORROBORATION" block (`promImpliedRuns`, `deadRunExcess`, `expectedResultConsumed`, `spawnReconExcess`, both `corroborationDetail.Add(...)` calls). Replace the `recon`/verdict tail with:

```csharp
// ── METRIC GATE (Phase 2 fills this in; Phase 1 leaves it inert) ──
var metricGateOk = true;                 // Phase 2: MG-1/MG-2/MG-3
var corroborationDetail = new List<string>();   // Phase 2: metric-gate failure lines
var recon = corroborationDetail.Count == 0
    ? ReconciliationOutcome.Reconciled
    : ReconciliationOutcome.Unreconciled;

// ── VERDICT (ES-binding ∧ value-chain ∧ metric gate) ──
var pass = missing == 0 && !dupFail && valueChainOk && metricGateOk;
var verdict = pass ? Verdict.Pass : Verdict.Fail;
```

Update the `Analyze` signature to drop `triggerCount` and `spawnExtra`:

```csharp
public AnalyzerReport Analyze(IReadOnlyList<RunTrace> runs, PromCounterSnapshot prom,
                              string scenarioId,
                              IReadOnlyDictionary<string, double>? tripDurationMsByExecution = null,
                              IReadOnlyDictionary<string, double>? tripDurationMsByCorrelation = null,
                              IReadOnlyDictionary<string, int>? seedsByExecution = null)
```

In the `return new AnalyzerReport { … }`, remove `TriggerCount`, `PromImpliedRuns`, `SpawnExtra`, `ExpectedResultConsumed` assignments.

- [ ] **Step 4: Strip the retired fields from `AnalyzerReport.cs`.** Delete the `TriggerCount`, `PromImpliedRuns`, `SpawnExtra`, `ExpectedResultConsumed` properties. Keep `Reconciliation`, `CorroborationDetail`, `Prom`.

- [ ] **Step 5: Update the live fixture call.** In `AnalyzerE2ETests.cs` remove the `triggerCount`/`spawnExtra` derivation (lines ~165–200) and the `triggerCount`/`spawnExtra` args in the `Analyze(...)` call. Keep the fan-out precondition assert but repoint it to use `promSnapshot.OrchestratorMessagesSentDelta != 0 || traces.Count > 0`. Replace the metric query string literals `orchestrator_messages_sent_total` etc. in `ReadCounterSetAsync` with the `LiveMetricNames.*` consts.

- [ ] **Step 6: Build and run hermetic facts**

Run: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo && tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-class "*PassFailEngineFacts*/*PassFailEngineValueChainFacts*/*HarnessMetricDriftFacts*" --timeout 4m`
Expected: PASS, all succeeded, 0 build warnings.

- [ ] **Step 7: Commit**

```bash
git add tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
git commit -m "test(analyzer): retire miscalibrated Prom corroboration (impliedRuns/9, spawn-recon)"
```

- [ ] **Step 8: Live check (optional, ~10 min).** `pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-01`; read the report and confirm `Reconciliation: Reconciled` with no corroboration warnings, verdict PASS.

---

# PHASE 2 — A: binding metric gate at quiescence

Outcome: the verdict binds on result conservation, keeper-recovery activity (per-scenario), and probe liveness — evaluated after the harness drains to quiescence.

## Task 2.1: Add keeper deltas to the snapshot

**Files:**
- Modify: `tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs`

- [ ] **Step 1: Add the three fields**

```csharp
    /// <summary>keeper_messages_consumed_total — windowed delta. &gt; 0 proves the keeper consumed recovery work.</summary>
    public required double KeeperMessagesConsumedDelta { get; init; }

    /// <summary>keeper_messages_sent_total — windowed delta. &gt; 0 proves the keeper re-emitted recovery messages.</summary>
    public required double KeeperMessagesSentDelta { get; init; }

    /// <summary>rate(keeper_l2_probe_total[…]) — the BIT probe cadence. &gt; 0 proves the keeper is live and probing L2.</summary>
    public required double KeeperL2ProbeRate { get; init; }
```

- [ ] **Step 2: Update `CleanSnapshot()` in `PassFailEngineFacts.cs`** to set the three new fields to `0` (keeps facts compiling). Build:

Run: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo`
Expected: build errors only where `PromCounterSnapshot { … }` is constructed without the new required fields (the fixture's `BuildSnapshot`). This lists the call sites.

- [ ] **Step 3: Commit (will finish wiring in 2.3)** — skip; commit after 2.3 builds green.

## Task 2.2: The three metric gates (hermetic-first)

**Files:**
- Modify: `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`
- Modify: `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs`
- Modify: `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs`

- [ ] **Step 1: Write the failing facts** (append to `PassFailEngineFacts.cs`)

```csharp
    // Helper: a quiescent, conserving snapshot for N runs (result conservation holds exactly at drain).
    private static PromCounterSnapshot ConservingSnapshot(double results, double keeperConsumed = 0,
        double keeperSent = 0, double probeRate = 0.2) => new()
    {
        OrchestratorMessagesSentDelta = results * 2,            // ~2x results (two-consumer hop); not asserted
        OrchestratorMessagesConsumedDelta = results,           // == processor_sent at quiescence (MG-1)
        ProcessorMessagesConsumedDelta = results,
        ProcessorMessagesSentDelta = results,
        KeeperMessagesConsumedDelta = keeperConsumed,
        KeeperMessagesSentDelta = keeperSent,
        KeeperL2ProbeRate = probeRate,
    };

    [Fact]
    public void MetricGate_ConservationHolds_ProbeLive_Yields_Pass()
    {
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = ConservingSnapshot(results: 9);   // orch_consumed 9 == proc_sent 9
        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");
        Assert.True(report.MetricGate.ConservationOk);
        Assert.True(report.MetricGate.ProbeLiveOk);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void MetricGate_ConservationBroken_Yields_Fail()
    {
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var snap = ConservingSnapshot(results: 9) with { OrchestratorMessagesConsumedDelta = 9, ProcessorMessagesSentDelta = 4 };
        var report = new PassFailEngine().Analyze(new[] { run }, snap, "unit-test");
        Assert.False(report.MetricGate.ConservationOk);
        Assert.Equal(Verdict.Fail, report.Verdict);   // binding: real loss/leak
        Assert.NotEmpty(report.CorroborationDetail);
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo`
Expected: FAIL — `report.MetricGate` and the `expectsKeeperActivity` param don't exist yet.

- [ ] **Step 3: Add the `MetricGateResult` record to `AnalyzerReport.cs`**

```csharp
/// <summary>Phase 74+ binding metric gate (evaluated at quiescence). All three fold into the verdict.</summary>
public sealed record MetricGateResult
{
    /// <summary>MG-1: orchestrator_messages_consumed == processor_messages_sent (±1). False ⇒ real loss/leak.</summary>
    public required bool ConservationOk { get; init; }
    /// <summary>MG-2: for keeper-recovery scenarios, keeper_messages_consumed &gt; 0 AND sent &gt; 0 (else expected 0). </summary>
    public required bool KeeperRecoveryOk { get; init; }
    /// <summary>MG-3: rate(keeper_l2_probe) &gt; 0 — keeper alive and probing.</summary>
    public required bool ProbeLiveOk { get; init; }
    /// <summary>True iff this scenario's recovery path runs through the keeper (drives MG-2's direction).</summary>
    public required bool ExpectsKeeperActivity { get; init; }
}
```

Add to `AnalyzerReport`: `public required MetricGateResult MetricGate { get; init; }`.

- [ ] **Step 4: Implement the gate in `PassFailEngine.cs`.** Add `bool expectsKeeperActivity = false` as a param to `Analyze`. Replace the inert Phase-1 metric-gate block with:

```csharp
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
```

Set `MetricGate = metricGate` in the report constructor and keep `var pass = missing == 0 && !dupFail && valueChainOk && metricGateOk;`.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo && tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-class "*PassFailEngineFacts*" --timeout 4m`
Expected: PASS, all succeeded.

- [ ] **Step 6: Commit**

```bash
git add tests/BaseApi.Tests/Observability/Analysis/
git commit -m "test(analyzer): bind verdict on metric gate (conservation, keeper-recovery, probe)"
```

## Task 2.3: Wire keeper reads + drain into the fixture

**Files:**
- Modify: `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`
- Modify: `scripts/phase-67-harness.ps1`

- [ ] **Step 1: Extend `ReadCounterSetAsync` / `BuildSnapshot`** in `AnalyzerE2ETests.cs` to also query `LiveMetricNames.KeeperMessagesConsumedTotal`, `KeeperMessagesSentTotal` (windowed deltas, same pattern as the four existing) and `KeeperL2ProbeTotal` as a rate (`rate(keeper_l2_probe_total[2m])` instant query at windowEnd). Populate the three new `PromCounterSnapshot` fields. *(Follow the exact `ReadCounterSetAsync` shape already in the file; the probe rate uses the `PrometheusTestClient` instant-query helper with the `rate(...)` expression.)*

- [ ] **Step 2: Add the per-scenario keeper expectation.** Add a static map in `AnalyzerE2ETests.cs`, initially **all false** (reporting-only — calibrated in Step 5):

```csharp
// MG-2 calibration (Task 2.4): which scenarios recover THROUGH the keeper. Seeded false; set true after
// the first reporting-only sweep reveals non-zero keeper_messages_* deltas for a scenario.
private static readonly IReadOnlyDictionary<string, bool> ExpectsKeeperActivity =
    new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        ["TEST-01"] = false, ["TEST-02"] = false, ["TEST-03"] = false, ["TEST-04"] = false,
        ["TEST-05"] = false, ["TEST-06"] = false, ["TEST-07"] = false,
    };
```

Pass `expectsKeeperActivity: ExpectsKeeperActivity.GetValueOrDefault(scenarioId, false)` into `Analyze(...)`.

- [ ] **Step 3: Add drain-to-quiescence to the harness.** In `scripts/phase-67-harness.ps1`, after STEP F.5 (window close) and before STEP H (analyze), insert: stop the seeded workflow cron via `POST /api/v1/orchestration/stop` with the resolved `$wfId`, then poll `orchestrator_messages_sent_total` until two consecutive reads (≥20 s apart) are equal (counters flat), bounded 120 s. This makes MG-1 conservation hold (it only holds at quiescence).

```powershell
    # STEP F.6 — DRAIN TO QUIESCENCE (MG-1 conservation holds only when no message is in flight).
    Write-Phase "STEP F.6: stop workflow + drain to quiescence (for metric-gate conservation)"
    $stopBody = ConvertTo-Json @($wfId)
    try { Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/stop' `
            -ContentType 'application/json' -Body $stopBody -TimeoutSec 15 -ErrorAction Stop | Out-Null } catch { }
    $prev = -1; $stableDeadline = (Get-Date).AddSeconds(120)
    do {
        Start-Sleep -Seconds 20
        $cur = Get-FireCount
        if ($cur -eq $prev) { break }
        $prev = $cur
    } while ((Get-Date) -lt $stableDeadline)
    Write-Phase "  drained (orchestrator_messages_sent flat at $cur)." 'Gray'
```

*(Place this AFTER `$windowEnd` is recorded so the time-pinned Prom delta still spans the active window; the drain only ensures the counters have settled before the analyzer's pinned reads.)*

- [ ] **Step 4: Build green + commit**

Run: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo`
Expected: PASS, 0 warnings.

```bash
git add tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs scripts/phase-67-harness.ps1
git commit -m "test(analyzer): read keeper deltas + drain-to-quiescence; harness stop+settle"
```

## Task 2.4: Calibrate MG-2 (reporting-only sweep, then bind)

- [ ] **Step 1: Reporting-only sweep.** Run the full fault sweep:
  `pwsh -Command "& './scripts/phase-68-sweep.ps1' -ScenarioIds 'TEST-02','TEST-03','TEST-04','TEST-05','TEST-06','TEST-07'"`
  For each, read `…/analyzer-reports/TEST-0X.json` and record `Prom.KeeperMessagesConsumedDelta` / `…SentDelta`.

- [ ] **Step 2: Set the map.** In `ExpectsKeeperActivity`, set `true` for every scenario whose recorded keeper deltas were both `> 0`, leave `false` for the rest. Add a one-line comment per scenario with the observed delta as evidence.

- [ ] **Step 3: Re-run the sweep** and confirm **6/6 PASS** with MG-1/MG-2/MG-3 all satisfied (check each report's `MetricGate`).

- [ ] **Step 4: Commit**

```bash
git add tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
git commit -m "test(analyzer): calibrate MG-2 keeper-recovery expectation per scenario (evidence-based)"
```

---

# PHASE 3 — B: in-flight-vs-post-recovery loss classification

Outcome: an L2-wipe that loses one in-flight execution is *tolerated and counted* (deterministic PASS); an execution that starts after recovery and fails to complete, or any duplicate, still FAILS.

## Task 3.1: Export the recovery timestamp from the harness

**Files:**
- Modify: `scripts/phase-67-harness.ps1`

- [ ] **Step 1: Capture and export `RECOVERY_UTC`.** In STEP F.4 (post-start health-wait), after a fault scenario's crashed tier returns healthy, set `$recoveryUtc = [DateTimeOffset]::UtcNow`. For the no-fault baseline leave it null. In STEP H, export it alongside the window seam:

```powershell
    $env:RECOVERY_UTC = if ($recoveryUtc) { $recoveryUtc.ToString('o') } else { '' }
```

Add `Env:RECOVERY_UTC` to the `Remove-Item Env:…` cleanup in the `finally`.

- [ ] **Step 2: Commit**

```bash
git add scripts/phase-67-harness.ps1
git commit -m "test(harness): export RECOVERY_UTC (recovered-healthy instant) for loss classification"
```

## Task 3.2: Classify incomplete runs in the engine (hermetic-first)

**Files:**
- Modify: `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`
- Modify: `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs`
- Modify: `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs`

- [ ] **Step 1: Write the failing facts**

```csharp
    private static readonly string[] Missing4Hops = { "Step_A", "Step_B", "Step_C", "Step_D1" }; // started, stalled

    [Fact]
    public void InFlight_LossBeforeRecovery_IsTolerated_Yields_Pass()
    {
        // One complete run + one run that stalled BEFORE recovery (its last hop precedes RECOVERY_UTC).
        var complete = RunTrace.FromLabels("corr-1", "exec-1", AllTenLabelsWithConvergentGx2);
        var stalled  = RunTrace.FromLabels("corr-2", "exec-2", Missing4Hops);
        var recovery = DateTimeOffset.Parse("2026-06-18T10:00:30Z");
        var lastHop  = new Dictionary<string, DateTimeOffset> { ["corr-2|exec-2"] = DateTimeOffset.Parse("2026-06-18T10:00:10Z") };
        var firstHop = new Dictionary<string, DateTimeOffset> { ["corr-2|exec-2"] = DateTimeOffset.Parse("2026-06-18T10:00:05Z") };
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(new[] { complete, stalled }, snap, "TEST-05",
            recoveryUtc: recovery, firstHopUtcByExecution: firstHop, lastHopUtcByExecution: lastHop);

        Assert.Equal(1, report.InFlightLoss);     // counted
        Assert.Equal(0, report.Missing);          // NOT a binding miss
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void Incomplete_StartedAfterRecovery_Yields_Fail()
    {
        // A run that FIRST logged AFTER recovery and still didn't finish → recovery is broken → FAIL.
        var stalled  = RunTrace.FromLabels("corr-9", "exec-9", Missing4Hops);
        var recovery = DateTimeOffset.Parse("2026-06-18T10:00:30Z");
        var lastHop  = new Dictionary<string, DateTimeOffset> { ["corr-9|exec-9"] = DateTimeOffset.Parse("2026-06-18T10:01:10Z") };
        var firstHop = new Dictionary<string, DateTimeOffset> { ["corr-9|exec-9"] = DateTimeOffset.Parse("2026-06-18T10:01:05Z") };
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(new[] { stalled }, snap, "TEST-05",
            recoveryUtc: recovery, firstHopUtcByExecution: firstHop, lastHopUtcByExecution: lastHop);

        Assert.Equal(1, report.Missing);          // post-recovery incomplete is binding
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public void InFlight_LossExceedsBound_Yields_Fail()
    {
        // More in-flight losses than MaxInFlightLoss (default 4) → FAIL (something worse than a single wipe-window).
        var runs = Enumerable.Range(1, 6)
            .Select(i => RunTrace.FromLabels($"corr-{i}", $"exec-{i}", Missing4Hops)).ToArray();
        var recovery = DateTimeOffset.Parse("2026-06-18T10:00:30Z");
        var last  = runs.ToDictionary(r => $"{r.CorrelationId}|{r.ExecutionId}", _ => DateTimeOffset.Parse("2026-06-18T10:00:10Z"));
        var first = runs.ToDictionary(r => $"{r.CorrelationId}|{r.ExecutionId}", _ => DateTimeOffset.Parse("2026-06-18T10:00:05Z"));
        var snap = ConservingSnapshot(results: 9);

        var report = new PassFailEngine().Analyze(runs, snap, "TEST-05",
            recoveryUtc: recovery, firstHopUtcByExecution: first, lastHopUtcByExecution: last);

        Assert.True(report.InFlightLoss > 4);
        Assert.Equal(Verdict.Fail, report.Verdict);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo`
Expected: FAIL — the new `Analyze` params and `report.InFlightLoss` don't exist.

- [ ] **Step 3: Add report fields** (`AnalyzerReport.cs`):

```csharp
    /// <summary>B-criterion: started-but-incomplete runs whose last hop precedes RECOVERY_UTC — tolerated
    /// in-flight-at-wipe losses (counted, do NOT fail unless &gt; MaxInFlightLoss). Distinct from Missing.</summary>
    public required int InFlightLoss { get; init; }

    /// <summary>Per-run evidence for each tolerated in-flight loss (corr|exec + last-hop vs recovery).</summary>
    public required IReadOnlyList<string> InFlightLossDetail { get; init; }
```

- [ ] **Step 4: Implement classification in `PassFailEngine.cs`.** Add params `DateTimeOffset? recoveryUtc = null, IReadOnlyDictionary<string, DateTimeOffset>? firstHopUtcByExecution = null, IReadOnlyDictionary<string, DateTimeOffset>? lastHopUtcByExecution = null, int maxInFlightLoss = 4`. Replace the `missing` computation:

```csharp
const int MaxInFlightLossDefault = 4;
var maxLoss = maxInFlightLoss;
var firstHop = firstHopUtcByExecution ?? new Dictionary<string, DateTimeOffset>();
var lastHop  = lastHopUtcByExecution  ?? new Dictionary<string, DateTimeOffset>();

var incomplete = runs.Where(r => !IsComplete(r)).ToList();
var inFlightLoss = 0; var bindingMissing = 0;
var inFlightLossDetail = new List<string>(); var missingDetail = new List<string>();

foreach (var r in incomplete)
{
    var key = $"{r.CorrelationId}|{r.ExecutionId}";
    var startedAfterRecovery = recoveryUtc is { } rec
        && firstHop.TryGetValue(key, out var fh) && fh > rec;
    var stalledBeforeRecovery = recoveryUtc is { } rec2
        && lastHop.TryGetValue(key, out var lh) && lh < rec2;

    if (stalledBeforeRecovery && !startedAfterRecovery)
    {
        inFlightLoss++;
        inFlightLossDetail.Add($"[{key}] in-flight loss: last hop {lastHop[key]:o} < recovery {recoveryUtc:o} (tolerated).");
    }
    else
    {
        bindingMissing++;
        missingDetail.Add($"[{key}] started-but-incomplete and NOT an in-flight-at-wipe loss → binding miss.");
    }
}

var missing = bindingMissing;
var inFlightOverBound = inFlightLoss > maxLoss;
if (inFlightOverBound)
    missingDetail.Add($"in-flight loss {inFlightLoss} exceeds MaxInFlightLoss {maxLoss} — worse than a single wipe window.");
```

Update the verdict: `var pass = missing == 0 && !inFlightOverBound && !dupFail && valueChainOk && metricGateOk;`. Keep `CompleteRuns = complete.Count` (compute `complete` as before). Set `InFlightLoss = inFlightLoss`, `InFlightLossDetail = inFlightLossDetail` in the report. Delete the old inline `missing`/`missingDetail` block this replaces.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo && tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-class "*PassFailEngineFacts*/*PassFailEngineValueChainFacts*" --timeout 4m`
Expected: PASS, all succeeded.

- [ ] **Step 6: Commit**

```bash
git add tests/BaseApi.Tests/Observability/Analysis/
git commit -m "test(analyzer): classify in-flight (tolerated) vs post-recovery (binding) loss"
```

## Task 3.3: Feed first/last hop timestamps from the fixture

**Files:**
- Modify: `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`

- [ ] **Step 1: Compute per-(corr,exec) first/last hop UTC.** In `BuildRunTraces` (which already tracks `@timestamp` for trip-duration `spanByExecution`), also emit `firstHopUtcByExecution` (the min `@timestamp`) and `lastHopUtcByExecution` (the max) keyed `"corr|exec"`. Surface them on the returned cohort tuple.

- [ ] **Step 2: Parse `RECOVERY_UTC` and pass through.** Add `TryParseUtc(Environment.GetEnvironmentVariable("RECOVERY_UTC"), out var recoveryUtc)`; pass `recoveryUtc` (nullable), `firstHopUtcByExecution`, `lastHopUtcByExecution` into `Analyze(...)`.

- [ ] **Step 3: Build green + commit**

Run: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo`
Expected: PASS, 0 warnings.

```bash
git add tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
git commit -m "test(analyzer): feed RECOVERY_UTC + first/last hop timestamps into classification"
```

## Task 3.4: Targeted live validation of B

- [ ] **Step 1: Force an in-flight loss.** With the stack up + workflow firing, crash Redis timed to land while dispatches flow (poll `orchestrator_messages_sent_total`, `docker compose stop redis` on increment, 8 s dwell, `start`). Repeat until an ES `(corr,exec)` shows <9 hops with its last hop before recovery. *(Method validated this cycle — sub-second window, may take several attempts.)*

- [ ] **Step 2: Run the analyzer** for that window (set `SCENARIO_ID=TEST-05`, `WINDOW_*_UTC`, `RECOVERY_UTC`) and confirm the report shows `InFlightLoss ≥ 1`, `Missing == 0`, `Verdict: Pass`.

- [ ] **Step 3: Full sweep regression.** Re-run all 7 (`phase-68-sweep` with the array form) and confirm 7/7 PASS with the metric gate + classification active. Read `analyzer-reports/phase-68-summary.json`.

- [ ] **Step 4: Commit any map/bound tweaks** (e.g. `MaxInFlightLoss` if observed in-flight concurrency justifies a different bound), with the observed evidence in the commit message.

---

## Self-review checklist (completed)

- **Spec coverage:** Increment 1 → Phase 1 (drift guard 1.1/1.2, retire corroboration 1.3). Increment 2 (MG-1/2/3 + drain) → Phase 2 (2.1 snapshot, 2.2 gate, 2.3 wire+drain, 2.4 calibrate). Increment 3 (B classification) → Phase 3 (3.1 RECOVERY_UTC, 3.2 classify, 3.3 feed timestamps, 3.4 validate). `src/` untouched constraint honored (drift-guard reads only). ✓
- **Placeholders:** none — every code step shows complete C#/PowerShell. The two genuinely deferred values (per-scenario keeper map, `MaxInFlightLoss`) are explicit *calibration tasks* (2.4, 3.4) with default values shipped, not TBDs. ✓
- **Type consistency:** `MetricGateResult` (ConservationOk/KeeperRecoveryOk/ProbeLiveOk/ExpectsKeeperActivity), `PromCounterSnapshot` keeper fields, and the `Analyze` signature additions (`expectsKeeperActivity`, `recoveryUtc`, `firstHopUtcByExecution`, `lastHopUtcByExecution`, `maxInFlightLoss`) are used identically across Phase 2/3 tasks and facts. `LiveMetricNames.*` consts match the field doc strings. ✓
