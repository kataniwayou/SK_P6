---
phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
plan: 01
subsystem: testing
tags: [analyzer, elasticsearch, resilience-sweep, es-query, xunit, observability]

# Dependency graph
requires:
  - phase: 77-consistent-framework-logging-model-scope-carried-execution-i
    provides: "Phase-77 log shape — entry record carries no attributes.ExecutionId (empty GUID skipped by scope); keeper reinject records gained attributes.StepId+MessageId"
provides:
  - "PassFailEngine with the entry-marker predicate deleted (exists attributes.ExecutionId ES filter is the sole exclusion)"
  - "Structural ES query (BuildStepSearchBody) that excludes keeper reinject records via must_not exists attributes.ReinjectOutcome"
affects: [78-02, 78-03, 78-04, resilience-sweep, live-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Query-level cohort exclusion (must_not exists) instead of engine-level predicate — moves entry-marker/keeper discrimination upstream into the ES query"

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs - deleted IsEntryMarker + its scored guard"
    - "tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs - deleted IsEntryMarkerExecution + 4 guards; added keeper must_not to BuildStepSearchBody"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs - deleted the D09 entry-marker fact"

key-decisions:
  - "D-01 delete (not adapt-and-keep): the entry-marker predicate is deleted everywhere; the exists attributes.ExecutionId ES filter is now the sole entry-marker exclusion"
  - "D-02 query-level exclusion: keeper reinject records are excluded from the structural cohort via must_not exists attributes.ReinjectOutcome on BuildStepSearchBody only"

patterns-established:
  - "Symmetric keeper queries: BuildStepSearchBody forbids attributes.ReinjectOutcome; BuildKeeperOutcomeSearchBody requires it"

requirements-completed: [D-01, D-02]

# Metrics
duration: 5min
completed: 2026-07-16
---

# Phase 78 Plan 01: Reader-side entry-marker delete + keeper query exclusion Summary

**Deleted the analyzer's entry-marker detector (D-01) so the `exists attributes.ExecutionId` ES filter is the sole exclusion, and added `must_not exists attributes.ReinjectOutcome` to the structural query (D-02) so Phase-77 keeper reinject records never enter the structural completeness cohort.**

## Performance

- **Duration:** 5 min
- **Started:** 2026-07-16T20:04:36Z
- **Completed:** 2026-07-16T20:09:40Z
- **Tasks:** 2 completed
- **Files modified:** 3

## Accomplishments
- Removed the `IsEntryMarker`/`IsEntryMarkerExecution` machinery end-to-end (engine + fixture + D09 fact), replacing it with the free, upstream `exists attributes.ExecutionId` ES query filter — a complete no-behaviour-change per RESEARCH Q1 (the `Step_A`/`M_1` edge was already dead today).
- Added the keeper `must_not exists attributes.ReinjectOutcome` clause to `BuildStepSearchBody` (bare field-path, no `.keyword` trap), scoped to the structural query only, keeping it symmetric with `BuildKeeperOutcomeSearchBody`.
- Tree stays green: 41/41 analyzer facts pass; 0-warning Debug + Release build.

## Task Commits

Each task was committed atomically:

1. **Task 1: Delete the entry-marker detector (D-01)** - `1daa8f6` (refactor)
2. **Task 2: Add the keeper must_not to the structural query (D-02)** - `02cd16d` (feat)

**Plan metadata:** (docs: complete plan — see final commit)

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` - Deleted the `IsEntryMarker` method + its doc; changed `scored = runs.Where(!IsEntryMarker).ToList()` to `var scored = runs.ToList();`.
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` - Deleted the `IsEntryMarkerExecution` method and all four call-site guards (structural no-MessageId branch, structural MessageId branch, dispatch loop, value loop guard line); deleted the write-only `entryMarkerCorrelations` set; added the keeper `must_not` block to `BuildStepSearchBody`.
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` - Deleted `PassFailEngine_EntryMarker_EmptyExecutionId_Excluded_From_Scoring_D09` (the engine no longer excludes `Guid.Empty`).

## Decisions Made
- Followed the locked D-01/D-02 decisions and RESEARCH inventory exactly. D-01 delete was confirmed GO by RESEARCH Q1 (no fall-back to adapt-and-keep needed). The value-loop body (`:639-656`) and `IsEntryMarkerExecution`'s former callers there were left intact except the single guard line — the loop itself is Plan 02's deletion.

## Deviations from Plan
None - plan executed exactly as written.

**Note on Task 2 acceptance count:** the plan's acceptance criterion expected `grep -c "ReinjectOutcomeFieldPath" AnalyzerE2ETests.cs` == 2, but it returns 3. The third hit is a pre-existing `<see cref="EsIndexNames.ReinjectOutcomeFieldPath"/>` XML doc-comment reference (line 399) inside `BuildKeeperOutcomeSearchBody`'s documentation — it existed before this plan and was not counted by the planner. The two actual query interpolations (new `must_not` in `BuildStepSearchBody`, existing `exists` in `BuildKeeperOutcomeSearchBody`) are exactly as specified; `BuildKeeperOutcomeSearchBody` and `BuildDispatchSearchBody` are byte-unchanged. No functional deviation.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plan 02 (value-oracle report-field removal) and Plan 03 (value-oracle deletion + `ExpectedHopOffset` Hazard-C migration) can proceed. The value-loop body in `AnalyzerE2ETests.cs` remains in place for Plan 02/03 to delete.
- The live D-04 gate (reseed + 7-scenario sweep) is Docker-gated and out of scope for this hermetic plan; Docker/RealStack is unavailable in this sandbox, so only the hermetic analyzer facts were exercised (green).

## Self-Check: PASSED

All 3 modified files and the SUMMARY exist on disk; both task commits (`1daa8f6`, `02cd16d`) are present in git history.

---
*Phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr*
*Completed: 2026-07-16*
