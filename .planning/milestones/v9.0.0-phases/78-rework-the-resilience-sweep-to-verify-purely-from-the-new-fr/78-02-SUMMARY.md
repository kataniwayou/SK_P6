---
phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
plan: 02
subsystem: testing
tags: [analyzer, value-oracle, resilience-sweep, pass-fail-engine, xunit, observability, es-query]

# Dependency graph
requires:
  - phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
    plan: 01
    provides: "Entry-marker detector deleted; keeper reinject records excluded from the structural query"
provides:
  - "AnalyzerE2ETests fixture with the value-oracle ES query + value-parse path deleted (no StepLabel/Produced evidence fetched or parsed)"
  - "PassFailEngine with the concrete value-CHAIN machinery removed (CheckValueChain/ResolveSeed/seedsByExecution/path #1/WR-02 guard/value-chain loop gone); ANL-03 path #2 is the sole non-binding reconciliation"
  - "AnalyzerReport with the vestigial ValueChainOk/ValueChainDetail required members removed; TelemetryGap/TelemetryGapDetail retained"
affects: [78-03, 78-04, resilience-sweep, live-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Delete-not-dormant: the value axis is removed end-to-end (fixture + engine + facts + report shape) rather than left dormant — post-Phase-77 it returns nothing, so leaving it would be a vacuous-green trap"
    - "Verdict now stands on structural stepId completeness + ANL-03 framework-redundancy + the Prometheus metric gate alone"

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs - deleted BuildValueOracleSearchBody, the valueHits fetch, the valuesByInstance value loop, TryReadSum/TryReadProduced, the seedsByExec map + TraceCohort.SeedsByExecution field, and the seedsByExecution: Analyze argument"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs - deleted the seedsByExecution param, valueOracleSupplied, telemetry-gap path #1, the value-chain assertion loop, the WR-02 guard, the orphaned inFlightLossKeys skip-set, ResolveSeed, CheckValueChain, and && valueChainOk from the verdict; kept the ExpectedHopOffset scaffold"
    - "tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs - removed the required ValueChainOk/ValueChainDetail members; kept TelemetryGap/TelemetryGapDetail"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs - deleted the moot OracleAbsent_...ValueChainNotApplicable fact"
  deleted:
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs - the 375-line hermetic value-chain fact suite, deleted entirely (not skipped)"

key-decisions:
  - "D-03 (value oracle FULL DELETION): the value-oracle ES query, value-parse path, and engine value-CHAIN machinery are deleted end-to-end; ANL-03 framework-redundancy (path #2) is the sole non-binding reconciliation"
  - "Vestigial report fields REMOVED (not nulled): ValueChainOk/ValueChainDetail required members deleted from AnalyzerReport.cs + the Analyze initializer + BuildSummary, with the moot OracleAbsent fact deleted — all atomic in Task 2"
  - "Hazard C: the ExpectedHopOffset/valueOracleHopSet/ExpectedFor label-fallback completeness scaffold is intentionally RETAINED — it is removed atomically with its ~6 dependent hermetic facts in Plan 03"

patterns-established:
  - "The concrete value oracle is dark from both the fixture (no StepLabel/Produced fetch) and the engine (no seed/value chain); RunTrace.FromStepIds defaults its value/label maps empty so traces build cleanly with no value axis"

requirements-completed: [D-03]

# Metrics
duration: 16min
completed: 2026-07-16
---

# Phase 78 Plan 02: Delete the concrete value oracle (fixture + engine + facts) Summary

**Deleted the concrete value oracle end-to-end (D-03) — the value-oracle ES query and value-parse path from the fixture, and the value-CHAIN machinery (CheckValueChain, ResolveSeed, seedsByExecution, telemetry-gap path #1, the WR-02 vacuous-green guard, the value-chain loop, and the vestigial ValueChainOk/ValueChainDetail report fields) from the engine — leaving ANL-03 framework-redundancy as the sole non-binding reconciliation. The ExpectedHopOffset completeness scaffold is intentionally retained until Plan 03 (Hazard C).**

## Performance

- **Duration:** 16 min
- **Started:** 2026-07-16T20:13:53Z
- **Completed:** 2026-07-16T20:30:18Z
- **Tasks:** 2 completed
- **Files modified:** 4 (+ 1 deleted)

## Accomplishments
- Removed the value axis from `AnalyzerE2ETests.cs`: deleted `BuildValueOracleSearchBody`, the `valueHits` fetch, the `valuesByInstance` value-oracle loop, `TryReadSum`/`TryReadProduced`, the `seedsByExec` seed map + `TraceCohort.SeedsByExecution` field, and the `seedsByExecution:` argument on the `Analyze` call — traces now build from structural stepIds only (`RunTrace.FromStepIds` defaults the value/label maps empty).
- Removed the value-CHAIN machinery from `PassFailEngine.cs`: the `seedsByExecution` param, `valueOracleSupplied`, telemetry-gap path #1 (the `ResolveSeed` + `CheckValueChain` + Step_G-terminal-anchor block), the value-chain assertion loop, the WR-02 vacuous-green guard, the now-orphaned `inFlightLossKeys` skip-set, and `&& valueChainOk` from the verdict; deleted `ResolveSeed` and `CheckValueChain`.
- Kept ANL-03 telemetry-gap path #2 as the SOLE non-binding reconciliation, plus the metric gate (MG-1/2/3) and the three-class INCONCLUSIVE/FAIL/PASS verdict, all byte-unchanged.
- Removed the vestigial `ValueChainOk`/`ValueChainDetail` `required` members from `AnalyzerReport.cs` (delete-not-dormant), kept `TelemetryGap`/`TelemetryGapDetail` (path #2 populates them), and deleted the moot `OracleAbsent_...ValueChainNotApplicable` fact.
- Deleted `PassFailEngineValueChainFacts.cs` (375 lines) entirely — not skipped.
- Retained the `ExpectedHopOffset`/`valueOracleHopSet`/`ExpectedFor` label-fallback completeness scaffold so the ~6 surviving label-based `PassFailEngineFacts` stay green (removed atomically with the fact migration in Plan 03 — Hazard C).
- Tree stays green: `PassFailEngineFacts` 29/29 pass (was 30, minus the deleted OracleAbsent fact); 0-warning Debug + Release build.

## Task Commits

Each task was committed atomically:

1. **Task 1: Delete the value-oracle query + value-parse path from the fixture** - `4dfefdb` (refactor)
2. **Task 2: Delete the engine value-CHAIN machinery + PassFailEngineValueChainFacts.cs** - `9a2b696` (refactor)

**Plan metadata:** (docs: complete plan — see final commit)

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — value-oracle query/parse path deleted; the fixture fetches and parses no `StepLabel`/`Produced` evidence. `TryReadTimestamp`, `BuildKeeperOutcomeMap`/`BuildKeeperOutcomeSearchBody`, and the dispatch in-degree convergent derivation untouched.
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` — value-CHAIN machinery removed; `ExpectedHopOffset` scaffold retained.
- `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` — `ValueChainOk`/`ValueChainDetail` required members removed; `TelemetryGap`/`TelemetryGapDetail` retained.
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — moot `OracleAbsent` fact deleted.
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs` — DELETED entirely.

## Decisions Made
- Followed the locked D-03 decision and the RESEARCH delete/edit inventory + Hazard C guidance exactly. Removed the `ValueChainOk`/`ValueChainDetail` report fields rather than hardcoding them (RESEARCH Open Q2 → remove), consistent with the delete-not-dormant posture; the sweep wrapper (`phase-68-sweep.ps1`) reads neither field, so the report shape it consumes is unaffected.
- Retained the `ExpectedHopOffset` scaffold (Hazard C) — deleting it here would break ~6 surviving label-based facts that fall to the `observedUnion` fallback and score single stalled runs as vacuously complete. Its removal is atomic with the fact migration in Plan 03.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Dead code] Removed the orphaned `inFlightLossKeys` skip-set**
- **Found during:** Task 2
- **Issue:** `inFlightLossKeys` (declaration + comment + the `.Add(key)` call in the tolerance-classification loop) existed SOLELY as the skip-set consumed by the value-chain loop. Deleting the value-chain loop (a plan-specified deletion) left it populated-but-never-read — a vestigial write-only set, exactly the dormant-code smell D-03 exists to eliminate.
- **Fix:** Removed the declaration, its comment block, and the `inFlightLossKeys.Add(key)` line from the KEPT classification loop. The tolerance classification (`inFlightLoss++`, the drop/in-flight detail lines) is otherwise byte-unchanged.
- **Files modified:** tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
- **Commit:** 9a2b696

**2. [Rule 1 - Doc accuracy] Updated doc comments that referenced the deleted value chain**
- **Found during:** Tasks 1 & 2
- **Issue:** Several XML/inline doc comments still described the value-oracle path (the `valueHits` `<paramref>` on `BuildRunTraces` would have produced a CS1734 0-warning-build failure once the param was removed; the `TelemetryGap` doc, the `Verdict.Pass`/`Verdict.Fail` docs, the engine VERDICT doc item, the `BuildSummary` gapNote wording, and the surviving ANL-03 fact's comments all referenced the now-deleted value chain).
- **Fix:** Removed the `<paramref name="valueHits"/>` reference (build-blocking) and updated the narrative docs to describe the ANL-03-only reconciliation and the value-axis-dark verdict. No behavioural change.
- **Files modified:** AnalyzerE2ETests.cs, PassFailEngine.cs, AnalyzerReport.cs, PassFailEngineFacts.cs
- **Commits:** 4dfefdb, 9a2b696

## Authentication Gates
None.

## Issues Encountered
None.

## User Setup Required
None — no external service configuration required.

## Next Phase Readiness
- Plan 03 can proceed: it removes the retained `ExpectedHopOffset`/`valueOracleHopSet`/`ExpectedFor` label-fallback scaffold atomically with migrating the ~6 dependent label-based `PassFailEngineFacts` to explicit `expectedStepIdsByExecution` (Hazard C).
- The live D-04 gate (reseed + 7-scenario sweep) is Docker-gated and out of scope for this hermetic plan; Docker/RealStack is unavailable in this sandbox, so only the hermetic analyzer facts were exercised (green). The RealStack fixture (`AnalyzerE2ETests`) is compile-gated and confirmed to build 0-warning.

## Verification
- `PassFailEngineFacts` (hermetic): 29/29 pass (was 30 — the deleted moot OracleAbsent fact accounts for the delta).
- `BaseApi.Tests.Observability.Analysis` namespace (hermetic, non-RealStack): 29/29 pass; `PassFailEngineValueChainFacts` gone.
- 0-warning Debug + Release solution build (`-warnaserror`).
- Grep acceptance: `BuildValueOracleSearchBody`/`valueHits`/`SeedsByExecution`/`TryReadSum`/`TryReadProduced` = 0 in the fixture; `CheckValueChain`/`ResolveSeed`/`seedsByExecution`/`valueOracleSupplied`/`ValueChainOk`/`ValueChainDetail` = 0 in the engine; `ValueChainOk`/`ValueChainDetail` = 0 in the report; `TelemetryGap` and `ExpectedHopOffset`/`valueOracleHopSet` retained (>= 1).

## Self-Check: PASSED

All 4 modified files and the deleted `PassFailEngineValueChainFacts.cs` are in the expected state; both task commits (`4dfefdb`, `9a2b696`) are present in git history.

---
*Phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr*
*Completed: 2026-07-16*
