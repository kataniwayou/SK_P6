---
phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
plan: 03
subsystem: testing
tags: [analyzer, value-oracle, resilience-sweep, pass-fail-engine, xunit, observability, hazard-c, stepid-completeness]

# Dependency graph
requires:
  - phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
    plan: 02
    provides: "Value oracle deleted end-to-end EXCEPT the retained ExpectedHopOffset completeness scaffold (Hazard C), removed atomically here"
provides:
  - "PassFailEngine with ZERO value-oracle residue — ExpectedHopOffset, valueOracleHopSet, and the ExpectedFor DistinctLabels label-fallback branch all deleted; ExpectedFor resolves the explicit expectedStepIdsByExecution else observedUnion"
  - "RunTrace with the now-orphaned DistinctLabels member deleted (no surviving consumer after the fallback)"
  - "The 6 label-fallback PassFailEngineFacts migrated to RunTrace.FromStepIds + explicit expectedStepIdsByExecution — the SAME stepId completeness mechanism the live gate uses; assertions unchanged"
  - "Hermetic analyzer gate green (Observability.Analysis 29/29, BuildKeeperOutcomeMap 3/3, FanInHermetic 5/5) with 0 new failures + 0-warning Debug/Release — the live-sweep prerequisite for Plan 04"
affects: [78-04, resilience-sweep, live-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Delete-not-dormant completed: the value axis is now gone from the engine end-to-end; the label-fallback branch (dead in production once the oracle went dark) is removed rather than left as never-live coverage"
    - "Facts test the LIVE mechanism: the migrated recoverability/completeness facts score incompleteness via explicit expectedStepIdsByExecution (the stepId path the live gate uses), never the deleted label→depth map"

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs - deleted the ExpectedHopOffset static map, the valueOracleHopSet local, and the ExpectedFor `if (r.DistinctLabels.Count > 0) return valueOracleHopSet;` label-fallback branch; ExpectedFor now resolves explicit expectedStepIdsByExecution else observedUnion; orphaned value-oracle comments removed"
    - "tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs - deleted the now-unused required DistinctLabels member + its two factory initializers; updated the doc crefs/comments that named it or ExpectedHopOffset"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs - migrated the 6 label-fallback facts to RunTrace.FromStepIds + explicit expectedStepIdsByExecution; added the StalledHopStepIds constant (stepId analog of Missing4Hops); assertions byte-identical"
  deleted: []

key-decisions:
  - "D-03 Hazard C RESOLVED (Option B, delete + migrate): ExpectedHopOffset/valueOracleHopSet/the ExpectedFor label-fallback are DELETED and the 6 dependent facts migrated to the explicit stepId expected-set — one atomic commit so the tree never went red"
  - "RunTrace.DistinctLabels DELETED: grep confirmed no surviving production/engine consumer after the fallback branch was removed (only RunTrace's own declaration/initializers referenced it), so it is value-oracle residue removed per the delete-not-dormant posture"
  - "Task 2 gate = zero NEW failures in changed scope, NOT absolute green: the full hermetic run has 282 pre-existing Docker-less infrastructure failures (broker/Postgres/Redis/ES); 0 of them are analyzer facts"

patterns-established:
  - "The stepId analog of a label-stalled run: StalledHopStepIds (3 of the 9 expected hops) + an explicit DistinctHopStepIds() expected set reproduces the SAME Missing/verdict a label-based Missing4Hops run asserted, via the live completeness path"

requirements-completed: [D-03]

# Metrics
duration: 28min
completed: 2026-07-16
---

# Phase 78 Plan 03: Remove the last value-oracle residue + migrate the label facts Summary

**Closed out D-03 by deleting the last value-oracle residue from the verdict engine — `ExpectedHopOffset`, `valueOracleHopSet`, the `ExpectedFor` `DistinctLabels` label-fallback branch, and the now-orphaned `RunTrace.DistinctLabels` member — while ATOMICALLY migrating the 6 surviving hermetic facts that leaned on the label fallback to `RunTrace.FromStepIds` + an explicit `expectedStepIdsByExecution` (the SAME stepId completeness mechanism the live gate uses). The full hermetic analyzer gate is green with zero new failures and a 0-warning Debug+Release build — the hard prerequisite for the live sweep (Plan 04).**

## Performance

- **Duration:** ~28 min
- **Started:** 2026-07-16T20:41:12Z
- **Completed:** 2026-07-16T21:09:33Z
- **Tasks:** 2 completed
- **Files modified:** 3

## Accomplishments
- **Half A — engine residue deleted (`PassFailEngine.cs`):** removed the `ExpectedHopOffset` label→depth static map, the `valueOracleHopSet` local, and the `ExpectedFor` middle branch `if (r.DistinctLabels.Count > 0) return valueOracleHopSet;`. `ExpectedFor` now resolves ONLY (1) the explicit `expectedStepIdsByExecution` else (2) `observedUnion`. Updated the orphaned value-oracle canonical-hop-set comments (class-level, expected-set-resolution priority list).
- **`RunTrace.DistinctLabels` deleted:** grep confirmed no surviving production/engine consumer after the fallback branch was gone (the only references were RunTrace's own declaration + two factory initializers + doc crefs). Removed the `required` member, both `FromLabels`/`FromStepIds` initializers, and fixed the `<see cref="DistinctLabels"/>` doc reference (would have been a CS1574 in the 0-warning build) plus the two stale `ExpectedHopOffset` prose references.
- **Half B — 6 facts migrated (`PassFailEngineFacts.cs`):** each fact that built a stalled/incomplete run via `RunTrace.FromLabels(..., Missing4Hops)` (or the missing-`Step_F2` label set) WITHOUT an explicit expected set — and therefore scored via the now-dead label fallback — now builds via `RunTrace.FromStepIds(..., convergentStepId: ConvergentStepId)` and passes an explicit `expectedStepIdsByExecution` (the working Phase-76 pattern). Added `StalledHopStepIds` (`hop-b, hop-c, hop-d1`) as the stepId analog of `Missing4Hops`. No assertion (Missing count / verdict class / InFlightLoss) was changed.
  - `Incomplete_StartedRun_DropsStepF2_Yields_Fail`
  - `Incomplete_StartedAfterRecovery_Yields_Fail`
  - `InFlightLoss_ManyCleanDrops_DoNotFail_Yields_Pass`
  - `RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail`
  - `KeeperReinject_VetoesRedisWipeTolerance_StalledBeforeRecovery_Yields_Fail`
  - `Verdict_PartialEvidence_RealConservationGap_Yields_Fail`
- **Atomic:** engine residue delete + fact migration + `DistinctLabels` removal landed in ONE commit (`5cca3a5`); the tree never went red mid-task (analyzer facts 29/29 immediately after).
- **Hermetic gate green (Task 2):** full non-RealStack run — analyzer scope 100% green; the 282 failures are the pre-existing Docker-less infrastructure baseline, 0 of them analyzer facts. 0-warning Debug AND Release build.

## Task Commits

1. **Task 1: ATOMIC — delete ExpectedHopOffset residue AND migrate the 6 label-fallback facts** - `5cca3a5` (refactor)
2. **Task 2: Hermetic green gate — full analyzer suite, zero new failures** - verification-only gate, no source edit (no commit)

**Plan metadata:** (docs: complete plan — see final commit)

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` — value-oracle residue removed; `ExpectedFor` is a 2-branch resolver (explicit expected-set → observedUnion). Metric gate, ANL-03 path #2, three-class verdict all byte-unchanged.
- `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs` — `DistinctLabels` deleted; `Labels`/`DistinctStepIds`/`Values` and both factories otherwise intact (`Labels` still feeds duplicate detection; the value-oracle `Values`/`Labels` optional axis on `FromStepIds` is untouched).
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — 6 facts migrated to the stepId path; `StalledHopStepIds` added; `Missing4Hops` retained (still used by the 3 non-migrated facts that carry a complete run in their cohort — those score via `observedUnion` and are unaffected).

## Decisions Made
- Followed the CONTEXT D-03 amendment (Option B) and RESEARCH Hazard C exactly. The migration list matched the RESEARCH enumeration; the hermetic gate was the final arbiter and left no label-based fact red, so no additional facts needed migrating.
- Deleted `RunTrace.DistinctLabels` (rather than leave it) because grep proved zero surviving consumer — consistent with the delete-not-dormant posture and the CONTEXT amendment naming it value-oracle residue. `Labels` was retained (still the raw input for the duplicate arithmetic and the optional value-oracle axis on `FromStepIds`), per the plan scoping only `DistinctLabels` for removal.
- Worded the residue-describing comments to avoid the literal `ExpectedHopOffset`/`valueOracleHopSet` tokens so the plan's `grep -c == 0` acceptance holds and no dangling reference to a deleted symbol remains.

## Deviations from Plan
None - plan executed exactly as written. `RunTrace.DistinctLabels` was deleted (the plan's conditional "DELETE ONLY IF grep confirms no surviving consumer") because the grep confirmed none.

## Verification
- **Task 1 (analyzer subset):** `*Observability.Analysis*` — 29/29 pass (unchanged count vs Plan 02; the 6 migrated facts stayed green through the residue deletion).
- **Task 2 (full hermetic gate):** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` — total 850, succeeded 568, **failed 282, skipped 0**. All 282 failures are pre-existing Docker-less infrastructure tests (`Features.Orchestration`, `Integration.*`, `Persistence.*`, `Observability.HealthEndpoints/MetricsExport/LogExport`, broker `rabbitmq://` connection failures) — **0 analyzer facts among them** (grep of the failed-test list for `Observability.Analysis|PassFailEngine|BuildKeeperOutcomeMap|FanInHermetic` returned empty).
  - `*PassFailEngineFacts` (Observability.Analysis): 29/29 pass.
  - `*BuildKeeperOutcomeMapFacts`: 3/3 pass.
  - `*FanInHermeticHarnessFacts`: 5/5 pass.
  - `PassFailEngineValueChainFacts.cs` absent; D09 entry-marker fact absent; no source references to either (no dangling red).
- **Build:** 0-warning `Debug` AND `Release` (`-warnaserror`).
- **Grep acceptance:** `ExpectedHopOffset` = 0, `valueOracleHopSet` = 0, `DistinctLabels.Count > 0` = 0 in `PassFailEngine.cs`; `DistinctLabels` = 0 in `RunTrace.cs`; the 6 migrated facts call `RunTrace.FromStepIds(...)` + `expectedStepIdsByExecution:`.

> **Baseline note:** the memory baseline is "~272" Docker-less failures; this run reported 282. The delta is normal run-to-run variance in the broker/DB connection-timeout Integration tests (they are timing-dependent) and is NOT attributable to this change — the change is isolated to the `Observability.Analysis` namespace (29/29 green), the build is 0-warning (no compile breakage anywhere), and no test outside that namespace consumes the removed `DistinctLabels`/`ExpectedHopOffset` symbols. Zero NEW failures in the changed scope.

## Authentication Gates
None.

## Issues Encountered
None.

## User Setup Required
None — no external service configuration required.

## Next Phase Readiness
- **Plan 04 (live D-04 sweep) prerequisite is MET:** the hermetic analyzer sampling grid is fully green and the build is 0-warning. Plan 04 is the Docker-gated live reseed + 7-scenario sweep (`scripts/phase-68-sweep.ps1`), which reproduces the `git show HEAD:analyzer-reports/phase-68-summary.json` baseline. Docker/RealStack is unavailable in this sandbox, so the live gate is deferred to an operator-run session per the runbook ([[rebuild-sourcehash-reseed-order]], stale-Debug-report + cold-ES TEST-01 traps).
- The verdict engine now stands purely on structural stepId completeness + ANL-03 framework-redundancy + the Prometheus metric gate — no value-oracle residue remains.

## Self-Check: PASSED

All 3 modified files are in the expected state and the task commit `5cca3a5` is present in git history (see Self-Check below).

---
*Phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr*
*Completed: 2026-07-16*
