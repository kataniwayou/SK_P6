---
phase: 75-recovery-verdict-per-execution-drop-tunable-constants
plan: 01
subsystem: testing
tags: [pass-fail-engine, analyzer, resilience-sweep, recoverability-classifier, keeper-evidence, xunit, tdd]

# Dependency graph
requires:
  - phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model
    provides: The Phase-74 binding metric gate (MG-1/2/3) in PassFailEngine.Analyze that this rework leaves intact
  - phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
    provides: The value-chain binding check + inFlightLossKeys skip-set (commit 3028f43) preserved as D75-8
provides:
  - "PassFailEngine.Analyze is a pure function of per-(corr,exec) recoverability — no absolute in-flight bound"
  - "keeperOutcomeByExecution contract parameter (corr|exec -> drop/reinject) driving the recoverability classifier"
  - "AnalyzerReport.UnrecoverableLossDetail cause-labeled evidence for keeper-confirmed clean-absent drops"
  - "Four hermetic facts proving D75-2 (no bound) / D75-3 (keeper-drop tolerated + recoverable-lost FAIL) / D75-5 (redis-wipe path preserved)"
affects: [75-02, 75-03, 75-04, phase-68-sweep, AnalyzerE2ETests, ReinjectConsumer keeper instrumentation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Optional trailing default-null Analyze parameter (keeps all callers compiling, no behaviour change)"
    - "Per-(corr,exec) recoverability classifier keyed on keeper log evidence, replacing a tunable absolute bound"

key-files:
  created:
    - .planning/phases/75-recovery-verdict-per-execution-drop-tunable-constants/deferred-items.md
  modified:
    - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
    - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
    - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs

key-decisions:
  - "Removed maxInFlightLoss=4 entirely (param + inFlightOverBound + verdict term + BuildSummary line) — no absolute bound survives"
  - "Tolerance = keeper clean-absent DROP (oc==drop) OR redis-wipe timestamp path; everything else recoverable-but-lost is a binding miss"
  - "UnrecoverableLossDetail is a cause-labeled SUBSET of InFlightLossDetail (keeper drops only; redis-wipe losses excluded)"
  - "Kept the D75-5 timestamp heuristic verbatim so redis-crash TEST-05/07 (which produce no keeper drop log) stay tolerated"

patterns-established:
  - "Recoverability classifier: a green sweep reflects real recovery, never a firing rate or time slot"
  - "inFlightLossKeys populated for EVERY tolerated key so the D75-8 value-chain skip-set (3028f43) survives the rework"

requirements-completed: [D75-1, D75-2, D75-3, D75-5, D75-8]

# Metrics
duration: 33min
completed: 2026-07-15
---

# Phase 75 Plan 01: Per-execution recovery verdict — decouple the sweep from tunable constants Summary

**Reworked `PassFailEngine.Analyze` into a pure function of per-(correlationId, executionId) recoverability: deleted the absolute `MaxInFlightLoss=4` cron-rate-coupled bound and added a keeper-evidence recoverability classifier (keeper clean-absent DROP tolerated, recoverable-but-lost binding FAIL) while preserving the redis-wipe timestamp path and the 3028f43 value-chain skip-set.**

## Performance

- **Duration:** 33 min
- **Started:** 2026-07-15T11:11:56Z
- **Completed:** 2026-07-15T11:44:33Z
- **Tasks:** 2 (both TDD)
- **Files modified:** 3 (+ 1 deferred-items note created)

## Accomplishments
- D75-2: the verdict no longer references any absolute in-flight-loss count — `maxInFlightLoss` / `inFlightOverBound` are gone from the engine (grep-clean), so a green sweep can never depend on a benign cron firing rate or window size.
- D75-3: added a `keeperOutcomeByExecution` classifier — a started-but-incomplete run is tolerated iff the keeper logged a clean-absent DROP for its `(corr,exec)` (provably-unrecoverable), otherwise it is a recoverable-but-lost binding FAIL.
- D75-5: preserved the redis-wipe timestamp heuristic verbatim (stalled-before-recovery), so redis-crash TEST-05/07 — which produce no keeper drop log — stay tolerated by the durability boundary.
- D75-8: the `inFlightLossKeys` skip-set (commit 3028f43) survives — every tolerated key is still added to it and skipped in the value-chain loop; its reproducing fact stays green.
- D75-1: the per-`(corr,exec)` `startedRuns = runs.Count` denominator is verified and locked (comment).
- Extended `AnalyzerReport` with a required `UnrecoverableLossDetail` surfacing the cause-labeled keeper-drop lines.

## Task Commits

Each task was committed atomically (TDD RED → GREEN):

1. **Task 1: repurpose the tunable-bound fact + add D75-2/3/5 keeper-evidence facts (RED)** - `f36d9b5` (test)
2. **Task 2: remove MaxInFlightLoss + add keeper-evidence classifier + preserve 3028f43 skip-set (GREEN)** - `42ef0ac` (feat)

_Note: Task 1 (RED) produced a deliberate compile-error red — the facts reference `keeperOutcomeByExecution` before Task 2 adds the parameter. No refactor commit was needed; the GREEN implementation follows in-file conventions cleanly._

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` - Removed `maxInFlightLoss` param + `inFlightOverBound` usage + verdict term + BuildSummary param/line; added `keeperOutcomeByExecution` classifier (keeper clean-drop OR redis-wipe tolerance); accumulate `unrecoverableLossDetail`; D75-1 comment.
- `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` - Added required `UnrecoverableLossDetail` member; updated `InFlightLoss`/`InFlightLossDetail` doc-comments to drop the removed-bound reference.
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` - Deleted `InFlight_LossExceedsBound_Yields_Fail`; added four facts: `InFlightLoss_ManyCleanDrops_DoNotFail_Yields_Pass`, `KeeperDrop_MarksIncomplete_Tolerated_Yields_Pass`, `RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail`, `RedisWipe_StalledBeforeRecovery_NoKeeperDrop_Yields_Pass`.
- `.planning/phases/75-.../deferred-items.md` - Logged the out-of-scope pre-existing broker-connection failures (Docker-less sandbox).

## Decisions Made
- Removed `MaxInFlightLoss` completely rather than replacing it with a different absolute count (research anti-pattern: a green verdict must not depend on a cron-rate proxy).
- Tolerance condition combines two paths — keeper clean-absent DROP (`oc == "drop"`, evidence-based, D75-3) OR redis-wipe stalled-before-recovery (timestamp heuristic, D75-5) — because redis-crash scenarios have no keeper drop log (Pitfall 5).
- `UnrecoverableLossDetail` carries only the keeper `"drop"`-tolerated cause labels (a subset of `InFlightLossDetail`); redis-wipe timestamp-path losses stay only in `InFlightLossDetail`.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- The full hermetic suite (`--filter-not-trait Category=RealStack`) reports 810 total / 538 passed / **272 failed**, but every one of the 272 failures is a broker-connection failure (`Connection Failed: rabbitmq://…`, 892 such lines) in the Docker-less sandbox — MassTransit/RabbitMQ-dependent tests that are not tagged `Category=RealStack` and thus run under the hermetic filter without a broker. **Zero** analyzer facts are among the failures. This is a pre-existing environmental condition (consistent with STATE.md: phases 68/73/74 ran live/broker gates deferred-automated when Docker is unavailable), out of scope for this pure-class rework. Logged in `deferred-items.md`, not fixed. The 28 `*PassFailEngineFacts` + `*PassFailEngineValueChainFacts` are all GREEN; Debug and Release both build 0-warning.

## Verification
- `*PassFailEngineFacts` + `*PassFailEngineValueChainFacts`: **28/28 GREEN** (all four new facts, the D75-8 `ToleratedInFlightLoss_PartialTerminalOnly_NotValueChainFailed_Yields_Pass`, and the two backward-compat timestamp facts).
- D75-8 reproducing fact re-run in isolation: **GREEN**.
- `grep maxInFlightLoss|MaxInFlightLoss|inFlightOverBound` in `PassFailEngine.cs`: **no matches**.
- Debug build: **0 warning / 0 error**. Release build: **0 warning / 0 error**.
- Live `phase-68-sweep.ps1` / RealStack analyzer gate (D75-4 ES-join, D75-6 TTL) is deferred-automated (no Docker) — those requirements belong to later plans in this phase.

## Next Phase Readiness
- The pure verdict rework (D75-1/2/3/5/8) is complete and hermetically proven. `keeperOutcomeByExecution` is a live seam ready for Plan 03/04 to wire: `AnalyzerE2ETests` must ES-query keeper drop/reinject logs into a per-`(corr,exec)` map and pass it through, and `ReinjectConsumer` must widen its drop/success logs with the four join keys (D75-4). `UnrecoverableLossDetail` is already on the report for the live path to surface.
- No blockers for the pure logic; the live half (D75-4/D75-6/D75-7) awaits a Docker-up environment for the close gate.

## Self-Check: PASSED

All created/modified files exist on disk; both task commits (`f36d9b5`, `42ef0ac`) are present in git history.

---
*Phase: 75-recovery-verdict-per-execution-drop-tunable-constants*
*Completed: 2026-07-15*
