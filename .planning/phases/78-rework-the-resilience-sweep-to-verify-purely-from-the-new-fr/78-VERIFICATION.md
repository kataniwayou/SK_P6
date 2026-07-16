---
phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
verified: 2026-07-16T22:51:06Z
status: gaps_found
score: 11/12 must-haves verified
overrides_applied: 0
gaps:
  - truth: "All 7 live scenarios (TEST-01..07) reproduce their committed-HEAD baseline verdict (all PASS), reconstructed from framework ES logs alone (D-04)"
    status: failed
    reason: >
      The D-04 live gate WAS executed end-to-end (mandatory SourceHash reseed + 7-scenario
      scripts/phase-68-sweep.ps1 live on Docker, ~3.5h) but did NOT reproduce the HEAD baseline.
      On fresh Phase-77 live data the reworked analyzer flips EVERY scenario PASS->FAIL, including
      the no-fault baseline TEST-01, by flagging 100% of executions as effect-once/duplicate
      violations (Duplicates == StartedRuns == CompleteRuns, Missing == 0, InFlightLoss == 0, value
      axis fully dark). TEST-07 is INDETERMINATE (analyze killed mid-run; on-disk copy is a stale
      2026-07-16 report and must not be read as this run's result). This is a genuine verdict SHIFT,
      correctly surfaced as a finding per the runbook (not silently re-baselined, not forced green).
    artifacts:
      - path: "analyzer-reports/phase-68-summary.json"
        issue: "6 of 7 scenarios verdict=Fail (all with Duplicates==StartedRuns), 1 INDETERMINATE, vs HEAD baseline of 7/7 Pass"
      - path: "tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs"
        issue: "BuildStepSearchBody's structural cohort query does not collapse multiple StepId-scoped framework records per {executionId, stepId} hop into one step execution — Phase 77's uniform execution-scope logging now emits multiple records per hop (dispatch-send, dispatch-consume, result-send, result-consume, keeper recovery-consume), each counted as a distinct step execution by the reworked D-01/D-02/D-03 structural query, tripping the effect-once/duplicate check on every hop of every live run"
    missing:
      - "A step-query/cohort de-duplication that collapses multiple execution-scoped framework log records per {executionId, stepId} to a single step execution (e.g. select only the canonical record kind, or de-dup by (executionId, stepId) before counting) — the hermetic RunTrace.FromStepIds facts feed exactly one record per stepId, a shape the live pipeline no longer produces, so this gap is invisible hermetically and only surfaces live"
      - "Re-run of the SourceHash reseed + 7-scenario live gate after the fix, confirming 7/7 verdict reproduction against the HEAD baseline (git show HEAD:analyzer-reports/phase-68-summary.json)"
---

# Phase 78: Rework the resilience sweep to verify purely from the new framework ES logs Verification Report

**Phase Goal:** Adapt the analyzer to the Phase-77 consistent-logging model so the resilience verdict reconstructs PURELY from framework ES logs — D-01 (entry-marker self-exclusion via ABSENT ExecutionId), D-02 (keeper reinject `must_not` discrimination), D-03 (drop the concrete value oracle), D-04 (re-run the SourceHash reseed + 7-scenario live gate and confirm 7/7 by ES logs). Keep Prometheus as the secondary collector-blind axis.
**Verified:** 2026-07-16T22:51:06Z
**Status:** gaps_found
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | The entry marker is excluded ONLY by the `exists attributes.ExecutionId` ES query filter — no engine-level `IsEntryMarker` predicate remains (D-01) | ✓ VERIFIED | `grep -c "IsEntryMarker" PassFailEngine.cs` = 0; `var scored = runs.ToList();` present at PassFailEngine.cs:122; `grep -c "IsEntryMarkerExecution\|entryMarkerCorrelations" AnalyzerE2ETests.cs` = 0; commit `1daa8f6` |
| 2 | Keeper reinject records never enter the structural completeness cohort — structural query forbids `attributes.ReinjectOutcome` (D-02) | ✓ VERIFIED | `AnalyzerE2ETests.cs:290-291` contains `"must_not": [{ "exists": { "field": "{{EsIndexNames.ReinjectOutcomeFieldPath}}" } }]` inside `BuildStepSearchBody`; `BuildKeeperOutcomeSearchBody` keeps the symmetric `exists` requirement (:383); commit `02cd16d` |
| 3 | The hermetic analyzer facts compile and run green (D09 entry-marker fact removed, not left dangling) | ✓ VERIFIED | `grep -c "EntryMarker_EmptyExecutionId_Excluded" PassFailEngineFacts.cs` = 0; live re-run this session: `Observability.Analysis` namespace 29/29 pass, 0 failed, 309ms |
| 4 | The value-oracle ES query and value-parse path are gone from the fixture — no StepLabel/Produced evidence fetched or parsed | ✓ VERIFIED | `grep -c "BuildValueOracleSearchBody\|valueHits\|SeedsByExecution\|seedsByExec\|TryReadSum\|TryReadProduced" AnalyzerE2ETests.cs` = 0; `TryReadTimestamp`/`BuildKeeperOutcomeMap`/`BuildKeeperOutcomeSearchBody` retained; commit `4dfefdb` |
| 5 | The engine's value-CHAIN machinery is gone (CheckValueChain, ResolveSeed, seedsByExecution param, telemetry-gap path #1, WR-02 guard, value-chain loop) | ✓ VERIFIED | `grep -c "CheckValueChain\|ResolveSeed\|seedsByExecution\|valueOracleSupplied\|ValueChainOk\|ValueChainDetail" PassFailEngine.cs` = 0; commit `9a2b696` |
| 6 | ANL-03 framework-redundancy (telemetry-gap path #2) survives as the SOLE non-binding reconciliation | ✓ VERIFIED | `PassFailEngine.cs:201-218` — "FRAMEWORK-REDUNDANCY RECONCILIATION (ANL-03, NO seed oracle required) — SOLE non-binding path" comment + logic intact and byte-unchanged per SUMMARY |
| 7 | `PassFailEngineValueChainFacts.cs` is deleted entirely (375 lines) — not skipped | ✓ VERIFIED | `ls tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs` → "No such file or directory" |
| 8 | The `ValueChainOk`/`ValueChainDetail` required members are removed from `AnalyzerReport.cs`; `TelemetryGap`/`TelemetryGapDetail` are retained | ✓ VERIFIED | `grep -c "ValueChainOk\|ValueChainDetail" AnalyzerReport.cs` = 0; `TelemetryGap` present (path #2 populates it) |
| 9 | The last value-oracle residue is gone from the engine — no `ExpectedHopOffset`, no `valueOracleHopSet`, no `DistinctLabels` label-fallback branch in `ExpectedFor` | ✓ VERIFIED | `grep -c "ExpectedHopOffset\|valueOracleHopSet" PassFailEngine.cs` = 0; `grep -n "DistinctLabels" RunTrace.cs` = empty (member deleted); commit `5cca3a5` |
| 10 | The ~6 facts that leaned on the label-fallback now assert the SAME stepId completeness mechanism the live gate uses (explicit `expectedStepIdsByExecution`) | ✓ VERIFIED | `PassFailEngineFacts.cs` contains 13 references to `expectedStepIdsByExecution` and 12 to `RunTrace.FromStepIds`; the 6 named facts (Incomplete_StartedRun_DropsStepF2..., etc.) migrated per SUMMARY, assertions unchanged |
| 11 | The SourceHash reseed runs FIRST (rebuild both host configs + docker compose build → graph-delete → seed → 204) before the sweep, and all 7 scenarios (TEST-01..07) reproduce their HEAD baseline verdict, reconstructed from framework ES logs ALONE | ✗ FAILED | Reseed WAS executed correctly (78-04-SUMMARY.md: graph-DELETE → seed → 204 confirmed, heal-wait trap hit and resolved). But verdict reproduction FAILED: 6/7 scenarios flip Pass→Fail (`Duplicates==StartedRuns` for all fresh scenarios), TEST-07 INDETERMINATE. See Gaps below. |

**Score:** 10/11 derived truths fully verified (D-01/D-02/D-03 truths 1-10 all VERIFIED); truth 11 (D-04 reseed+reproduction) is PARTIALLY true — reseed mechanics succeeded but baseline reproduction failed, which is the phase's terminal proof and is treated as FAILED for scoring purposes. Combined score: **11/12 must-haves** (counting the reseed-execution half of truth 11 as a separate verified sub-item, and the reproduction half as the single failed item).

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` | Pure verdict engine, entry-marker + value-chain + all value-oracle residue deleted | ✓ VERIFIED | All grep acceptance criteria from Plans 01-03 hold; hermetic facts pass |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` | ES→RunTrace fixture excluding keeper records + no value-oracle fetch/parse | ✓ VERIFIED | `must_not` clause present; value-oracle query/parse gone; structural/dispatch/keeper/trip-duration paths intact |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` | Hermetic facts, D09 removed, OracleAbsent fact removed, 6 facts migrated | ✓ VERIFIED | 29/29 pass live re-run this session |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs` | Deleted entirely | ✓ VERIFIED | File absent |
| `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` | `ValueChainOk`/`ValueChainDetail` removed, `TelemetryGap` retained | ✓ VERIFIED | grep confirms |
| `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs` | `DistinctLabels` removed (no surviving consumer) | ✓ VERIFIED | grep confirms |
| `analyzer-reports/phase-68-summary.json` | 7-scenario rerun roll-up reproducing HEAD baseline (all Pass) | ✗ FAILED (STUB-EQUIVALENT OUTCOME) | File exists and is a genuine fresh rerun (not stale/stub), but its content is 6× Fail + 1× INDETERMINATE vs the required all-Pass baseline — the artifact is honest and well-formed, but the underlying system behavior it reports does not meet the D-04 acceptance gate |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|----|--------|---------|
| `AnalyzerE2ETests.BuildStepSearchBody` | `EsIndexNames.ReinjectOutcomeFieldPath` | `must_not exists` clause | ✓ WIRED | Pattern `must_not[\s\S]*ReinjectOutcomeFieldPath` matches at `AnalyzerE2ETests.cs:290-291` |
| `PassFailEngine.Analyze` | telemetry-gap path #2 (ANL-03) | framework-redundancy reconciliation | ✓ WIRED | `PassFailEngine.cs:201-218`, comment + logic intact, referenced by surviving facts `PassFailEngine_ReconcileRedundancy_MissingHop`/`_MissingBoth`/`Verdict_NonZeroTelemetryGap_StaysPass_D01` |
| `PassFailEngine.ExpectedFor` | `expectedStepIdsByExecution` | explicit expected-set resolution (label fallback removed) | ✓ WIRED | `DistinctLabels.Count > 0` branch grep = 0; `expectedStepIdsByExecution` referenced 13× in facts, resolves to `ExpectedFor`'s primary branch |
| `scripts/phase-68-sweep.ps1` | `scripts/phase-67-harness.ps1` | per-scenario run-all no-fail-fast | ✓ WIRED (mechanically) / ✗ FAILED (outcome) | The sweep DID invoke the harness across all 7 scenarios (mechanical wiring intact — this is how the finding was produced), but the harness's own read was stale-shadowed for TEST-01 (Debug-shadow trap) and the aggregate outcome does not meet the D-04 gate |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|---------------------|--------|
| `analyzer-reports/{id}.json` (fresh Release reports TEST-01..06) | `Verdict`/`StartedRuns`/`CompleteRuns`/`Duplicates` | Live ES query via `BuildStepSearchBody` against the Docker compose stack | Yes — real, non-fabricated live ES data (mtime 2026-07-17 00:42..01:25, confirmed fresh) | ✓ FLOWING (but the data reveals the D-04 gap, not a hollow artifact) |
| `analyzer-reports/phase-68-summary.json` | roll-up of the 7 `{id}.json` | `phase-68-sweep.ps1` tabulation | Yes for TEST-01..06; TEST-07 is a stale prior-run copy, correctly flagged INDETERMINATE in the roll-up rather than silently reused | ✓ FLOWING (honest, not hollow) |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Hermetic analyzer facts (Observability.Analysis namespace) run green | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack --filter-namespace "*Observability.Analysis*"` | total 29, succeeded 29, failed 0, 309ms | ✓ PASS |
| D-01/D-02/D-03 commits exist in git history | `git show --stat 1daa8f6 / 02cd16d / 5cca3a5` | All 3 commits found with matching diffs (entry-marker delete, keeper must_not, ExpectedHopOffset residue delete) | ✓ PASS |
| D-04 live sweep produced fresh (non-fabricated) reports | Inspected `78-04-SUMMARY.md` mtime evidence + `analyzer-reports/phase-68-summary.json` content | 6 fresh Release reports + 1 correctly-flagged stale/INDETERMINATE; matches SUMMARY narrative exactly | ✓ PASS (confirms the finding is real, not a reporting artifact) |

Step 7b note: the D-04 live re-run itself was NOT re-executed by this verification (per the orchestrator's explicit instruction — it requires ~3.5h + a live Docker stack). The `78-04-SUMMARY.md` and the rerun `analyzer-reports/phase-68-summary.json` are treated as authoritative evidence, and their content was cross-checked for internal consistency (verdict table matches JSON, root-cause narrative matches the `Duplicates == StartedRuns` signature in the JSON).

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| D-01 | 78-01-PLAN.md | Entry-marker: delete the detector; `exists attributes.ExecutionId` is the sole exclusion | ✓ SATISFIED | Truths 1, commits `1daa8f6`; hermetic facts green |
| D-02 | 78-01-PLAN.md | Keeper discrimination: `must_not exists attributes.ReinjectOutcome` on the structural query | ✓ SATISFIED | Truth 2, commit `02cd16d`; symmetric with `BuildKeeperOutcomeSearchBody` |
| D-03 | 78-02-PLAN.md, 78-03-PLAN.md | Value oracle: full deletion (fixture query/parse, engine value-chain, `PassFailEngineValueChainFacts.cs`, `ExpectedHopOffset` residue, fact migration) | ✓ SATISFIED | Truths 4-10, commits `4dfefdb`/`9a2b696`/`5cca3a5`; hermetic gate green (29/29, 0 new failures vs baseline) |
| D-04 | 78-04-PLAN.md | Live-gate acceptance: reseed + 7-scenario sweep reproduces HEAD baseline (all 7 PASS) from framework ES logs alone | ✗ BLOCKED | 78-04-SUMMARY.md: reseed executed correctly, but 6/7 scenarios verdict-shifted Pass→Fail and 1/7 is INDETERMINATE. **Requirement D-04 remains OPEN** — root cause identified (structural step query does not collapse multi-record hops per `{executionId, stepId}`), but not yet fixed. |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| (none) | — | No TODO/FIXME/PLACEHOLDER/stub patterns found in the 5 modified analyzer files | — | Deletions were genuine, not stubbed-out or dormant-disabled |

### Human Verification Required

None. The D-04 outcome is a fully machine-produced, well-documented finding (fresh ES-backed reports, cross-checked against the HEAD baseline JSON, root cause identified with high confidence). No visual/UX/subjective judgment is needed — this is a clear-cut blocking regression requiring a code fix (step-query de-duplication), not human interpretation.

### Gaps Summary

**D-01, D-02, D-03 are fully and correctly implemented** — verified both by static grep/code inspection against every acceptance criterion in Plans 01-03, and by a live hermetic test re-run this session (`Observability.Analysis` namespace: 29/29 pass). All deletions are genuine (no dormant/stubbed-out code, no dangling references, no anti-patterns). The engine now stands purely on structural stepId completeness + ANL-03 framework-redundancy + the Prometheus metric gate, with zero value-oracle residue.

**D-04 is the phase's terminal proof and it FAILED.** The live gate was executed exactly per the runbook (reseed-first, the documented heal-wait trap was hit and correctly resolved via graph-DELETE→seed→204, the 7-scenario sweep ran on live Docker for ~3.5h). The result is a genuine, reproducible-in-principle regression: every fresh scenario (including the no-fault baseline TEST-01) is now scored FAIL because `Duplicates == StartedRuns` — the reworked structural step query is over-counting step executions on live data. This was correctly surfaced as a FINDING (not silently re-baselined, not forced green), which is exactly the behavior the D-04 plan mandated for a verdict SHIFT.

Root cause (high confidence, documented in `78-04-SUMMARY.md`): Phase 77's uniform execution-scope logging causes each hop to emit multiple `StepId`-scoped framework records (dispatch-send, dispatch-consume, result-send, result-consume, and — for recovered hops — keeper recovery-consume). The Phase-78 structural query (`BuildStepSearchBody`, as reworked by D-01/D-02) counts each such record as a distinct step execution, so the effect-once/duplicate check trips on every hop of every run. The hermetic test suite could not catch this because its `RunTrace.FromStepIds` facts synthesize exactly one record per stepId — a shape the live pipeline no longer produces post-Phase-77.

The fix belongs in the step query/cohort layer: collapse the multiple execution-scoped framework records per `{executionId, stepId}` to a single step execution (e.g., select only the canonical record kind per hop, or de-duplicate by `(executionId, stepId)` before counting). This is scoped, understood, and actionable — a follow-up closure plan for Phase 78 should target exactly this.

Per the phase's own D-04 acceptance language, **"a legitimate verdict SHIFT → STOP, treat as a finding (do not silently re-baseline)"** — this verification agrees with and upholds that call. The phase is NOT complete: D-01/D-02/D-03 shipped correctly, but the phase's actual purpose (proving the resilience verdict reconstructs purely and CORRECTLY from framework ES logs) is not yet achieved, since the reconstruction is currently wrong on live data.

---

*Verified: 2026-07-16T22:51:06Z*
*Verifier: Claude (gsd-verifier)*
