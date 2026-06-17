---
phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
plan: 03
subsystem: observability
tags: [observability, auditor, value-chain, convergent-terminal, fan-in, pure-model, hermetic-facts]

# Dependency graph
requires:
  - phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
    plan: 01
    provides: "ES-attribute contract field names Received / Produced (attributes.Produced is the per-step surfaced value the auditor reads into RunTrace.Values)"
provides:
  - "RunTrace: convergent-multiplicity awareness (HasIllegitimateDuplicate — Step_G x2 legitimate, x!=2 + any other duplicate fail) + per-step Values map; FromLabels gains an optional values param"
  - "PassFailEngine: 10-label AllLabels incl. Step_G; value-chain verdict (seed + hop-count, Step_G terminal seed+6 anchor proxy) folded into pass; Analyze gains trip-duration maps + per-exec seed map (all defaulted)"
  - "AnalyzerReport: ValueChainOk / ValueChainDetail / TripDurationMsByExecution / TripDurationMsByCorrelation required fields"
  - "PassFailEngineValueChainFacts: synthetic Pass + every Fail mode proven once over the shared FromLabels"
affects: [73-04, "AnalyzerE2ETests live fixture", "live ES auditor verdict"]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Convergent-terminal multiplicity exception: a single named label (Step_G) with expected multiplicity 2 is exempted from the fail-closed duplicate rule WITHOUT weakening it for any other label or for Step_G count != 2"
    - "Value-chain assertion as a binding verdict driver: Values[label] == seed + hop-count folds into pass = missing == 0 && !dupFail && valueChainOk; a tampered mid-hop or terminal value fails closed"
    - "Live terminal-anchor PROXY via ES log: Step_G completed-terminal value seed+6 stands in for the durable skp:out: blob (ES-read-only auditor cannot read Redis); the durable-blob proof is owned by the hermetic harness (Plan 02)"
    - "Pure-model shared construction proven once: both the live fixture and the hermetic facts build RunTrace via the extended FromLabels, so a green RealStack run is trustworthy not vacuous"

key-files:
  created:
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs - 8 synthetic facts: green (2 execs, Step_G x2 at 106/206) + every fail mode (missing label, Step_G count 1/3, wrong mid-chain value, wrong terminal value, non-convergent duplicate) + report-field wiring"
  modified:
    - "tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs - ConvergentLabel/ConvergentExpectedMultiplicity spec, HasIllegitimateDuplicate binding signal, required Values map, FromLabels(+optional values)"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs - 10-label AllLabels, HasIllegitimateDuplicate read, value-chain assertion (ExpectedHopOffset + ResolveSeed + CheckValueChain) folded into pass, Analyze trip-duration/seed params threaded through the report"
    - "tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs - ValueChainOk / ValueChainDetail / TripDurationMsByExecution / TripDurationMsByCorrelation required fields"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs - migrated 9 existing facts to the 10-label-with-Step_G-x2 cohort (Rule 3: the engine change made the old 9-label runs incomplete)"

key-decisions:
  - "LabelsPerRun kept at 9 (NOT 10): the inert Prom corroboration math basis; Step_G's x2 fan-in is intentionally NOT folded in because the binding verdict is value-chain + completeness, never Prom (metrics out-of-scope this phase)"
  - "Step_A carries hop-offset 0 (the seed itself); the value-chain check skips any run with an empty Values map so legacy callers (no value map) keep their pre-73 behaviour"
  - "Seed recovery order in ResolveSeed: explicit seedsByExecution['corr|exec'] -> Values['Step_B']-1 -> Values['Step_A'] -> 0, so the facts and the live fixture can both drive the check without a mandatory seed map"

patterns-established:
  - "A narrow, named convergent-multiplicity exception preserves the global fail-closed duplicate invariant (ASVS L1) while admitting one legitimate fan-in"
  - "ES-log value as a durable-anchor proxy when the durable store is unreadable by the auditor; the durable proof is split to a hermetic harness"

requirements-completed: [SPEC-R5, SPEC-R6]

# Metrics
duration: 15min
completed: 2026-06-17
---

# Phase 73 Plan 03: Pure Auditor Value-Chain + Convergent Terminal Summary

**The pure auditor (`RunTrace` / `PassFailEngine` / `AnalyzerReport`) now recognizes the 10-label set incl. the convergent terminal `Step_G` (expected multiplicity 2), folds a deterministic value-chain assertion (`seed + hop-count`, both `Step_G` arrivals at `seed+6` = the live terminal-anchor proxy) into the binding verdict, and carries per-(corr,exec)+per-corr trip duration — proven once by 8 synthetic engine facts, GREEN at 0-warning Debug + Release.**

## Final shared signatures (REQUIRED for Plan 04's live fixture)

Plan 04's `AnalyzerE2ETests` MUST call these exact shapes:

```csharp
// RunTrace.cs — values is optional; the live fixture fills it from attributes.Produced (73-01 contract).
public static RunTrace FromLabels(
    string correlationId, string executionId, IReadOnlyList<string> labels,
    IReadOnlyDictionary<string, int>? values = null)

// PassFailEngine.cs — every new param is defaulted; the live fixture passes the real trip-duration maps
// (and optionally a per-exec seed map keyed "correlationId|executionId").
public AnalyzerReport Analyze(
    IReadOnlyList<RunTrace> runs, PromCounterSnapshot prom,
    int triggerCount, string scenarioId, int spawnExtra = 0,
    IReadOnlyDictionary<string, double>? tripDurationMsByExecution = null,   // keyed "corr|exec"
    IReadOnlyDictionary<string, double>? tripDurationMsByCorrelation = null, // keyed "corr"
    IReadOnlyDictionary<string, int>? seedsByExecution = null)               // keyed "corr|exec"; else recovered from chain
```

Notes for Plan 04:
- The `Step_G` map entry must carry the terminal `seed+6` (106 for exec_a/seed 100, 206 for exec_b/seed 200) — the live terminal-anchor proxy. The auditor performs NO Redis `skp:out:` read; that durable-blob proof is owned by the hermetic harness (Plan 02).
- `Step_G` appears TWICE in the raw `labels` list (the per-arrival fan-in); the engine treats `Step_G` x2 as legitimate and any other duplicate or `Step_G` count != 2 as a fail.
- A run with an EMPTY `Values` map is NOT value-chain-checked (skipped), so a value-less call still behaves exactly as pre-73.

## Accomplishments

- **RunTrace** gained `ConvergentLabel` (`Step_G`) / `ConvergentExpectedMultiplicity` (2), the binding `HasIllegitimateDuplicate` flag (non-convergent count > 1 OR `Step_G` count != 2), a required `Values` map, and the extended `FromLabels`. Legacy `HasAnyDuplicateLabel` retained for report-shape stability.
- **PassFailEngine** now requires all 10 distinct labels for COMPLETE, reads `HasIllegitimateDuplicate` (not the raw flag) for the duplicate verdict, and folds `valueChainOk` into `pass = missing == 0 && !dupFail && valueChainOk` via `ExpectedHopOffset` + `ResolveSeed` + `CheckValueChain`. `LabelsPerRun` stays 9 (inert Prom basis); the entire Prom corroboration block is unchanged in structure and non-binding — no metric-counter assertions added.
- **AnalyzerReport** carries `ValueChainOk`, `ValueChainDetail`, `TripDurationMsByExecution`, `TripDurationMsByCorrelation`, all threaded through the `Analyze` initializer.
- **PassFailEngineValueChainFacts** (new, 8 facts) proves the green chain + every fail mode (missing label, `Step_G` count 1, `Step_G` count 3, wrong mid-chain value, wrong terminal value, non-convergent duplicate) and the report-field wiring.

## Task Commits

1. **Task 1: RunTrace convergent-multiplicity + per-step values** - `6d81532` (feat)
2. **Task 2: PassFailEngine 10-label + value-chain + AnalyzerReport fields** - `0451e76` (feat)
3. **Task 3: synthetic value-chain engine facts** - `bf0eb9f` (test)

## Verification

- `PassFailEngine.AllLabels` is the 10-label set incl. `Step_G`; COMPLETE requires all 10 distinct (verified via grep + facts).
- Duplicate verdict reads `HasIllegitimateDuplicate` (`Step_G` x2 legitimate; x!=2 + other duplicates fail) — proven by the `Step_G` count-1/count-3 and `Step_C` x2 fail facts.
- Value-chain check folds into `pass` (`&& valueChainOk` at line 248) with `Step_G` at `seed+6` as the live terminal-anchor proxy — proven by the wrong-mid-chain and wrong-terminal fail facts.
- `AnalyzerReport` carries `ValueChainOk` / `ValueChainDetail` + `TripDurationMsBy{Execution,Correlation}` (threaded + asserted by the report-wiring fact).
- 8/8 new facts GREEN + 17/17 full Analysis suite GREEN; **0-warning Debug + Release** (`-warnaserror`).

## Threat Mitigations Applied

- **T-73-05 (Tampering — value-chain verdict):** a mid-chain mutation (`Step_C=999`) and a wrong terminal (`Step_G=105 != 106`) both set `ValueChainOk=false` and fold into `Verdict.Fail` — proven by `Fail_WrongMidChainValue_StepC` + `Fail_WrongTerminalValue_StepG`.
- **T-73-06 (Spoofing/redelivery — `Step_G` x2 exception):** the convergent exception is narrow — only `Step_G` count == 2 is legitimate; `Step_G` count 3 (same-entryId redelivery) and `Step_C` x2 (non-convergent) still fail closed — proven by `Fail_StepGCountThree` + `Fail_NonConvergentDuplicate`. Fail-closed default preserved (ASVS L1).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Migrated existing PassFailEngineFacts to the 10-label cohort**
- **Found during:** Task 2 (AllLabels 9 -> 10)
- **Issue:** Making `Step_G` required for COMPLETE turned every 9-label run in the existing `PassFailEngineFacts` into started-but-incomplete (`Missing > 0`), flipping 7 of 9 facts from Pass to Fail at runtime (build still compiled — `FromLabels`'s new param is optional).
- **Fix:** Replaced the `AllNineLabels` helper with `AllTenLabelsWithConvergentGx2` (Step_A..F2 once + `Step_G` twice) and updated the 18 call sites + the now-stale `9-label`/`8-label`/`HasAnyDuplicateLabel` doc comments. These facts pass no value map, so the value-chain check skips them and each stays focused on its original branch.
- **Files modified:** tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
- **Verification:** all 9 migrated facts GREEN again (17/17 with the new facts).
- **Committed in:** `0451e76` (with Task 2).

**Total deviations:** 1 auto-fixed (1 blocking — engine change broke existing sibling facts).
**Impact on plan:** None on scope — `AnalyzerE2ETests` (the Docker-gated live fixture) compiles unchanged via the optional params and is extended by Plan 04; no `src/` edits.

## Known Stubs

None. The trip-duration maps default to empty when no live timestamps are supplied (by design — the engine reads no ES; the live fixture in Plan 04 passes the real maps), and the value-chain check is intentionally skipped for value-less (legacy) callers. These are documented contracts, not unwired stubs.

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs
- FOUND: tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs (HasIllegitimateDuplicate, Values, ConvergentLabel)
- FOUND: tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs ("Step_G", valueChainOk, && valueChainOk)
- FOUND: tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs (ValueChainOk, TripDurationMsBy*)
- FOUND commits: 6d81532, 0451e76, bf0eb9f

---
*Phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f*
*Completed: 2026-06-17*
