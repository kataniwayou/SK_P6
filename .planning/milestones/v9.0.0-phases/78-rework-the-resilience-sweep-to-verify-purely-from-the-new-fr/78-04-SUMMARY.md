---
phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
plan: 04
subsystem: testing
tags: [analyzer, resilience-sweep, live-gate, D-04, pass-fail-engine, effect-once, duplicates, finding, regression, reseed, stale-report]

# Dependency graph
requires:
  - phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
    plan: 03
    provides: "Hermetic analyzer gate green (value oracle deleted end-to-end, label facts migrated) — the live-sweep prerequisite"
provides:
  - "D-04 live gate EXECUTED — reseed-first runbook + 7-scenario sweep run live on Docker; result is a BLOCKING FINDING, not a clean pass"
  - "FINDING: the reworked live analyzer flips every scenario PASS->FAIL, including the NO-FAULT baseline (TEST-01), by flagging 100% of executions as effect-once (duplicate) violations — Duplicates == StartedRuns for all six fresh scenarios"
  - "Root cause (high confidence): against Phase 77's uniform execution-scope logging, each hop now emits multiple StepId-scoped framework records (send/consume/keeper-recovery); the reworked structural step query counts each as a distinct step execution -> systematic duplicate flags with the value axis dark"
  - "Fresh rerun roll-up analyzer-reports/phase-68-summary.json (replaces the stale single-TEST-07 working-tree copy)"
affects: [78, resilience-sweep, live-gate, D-04, pass-fail-engine, es-step-query]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "D-04 did its job: the hermetic suite (controlled single-record-per-stepId RunTrace facts) was green, but the LIVE gate exposed that multiple framework log records now exist per hop — a class of input the hermetic facts do not reproduce"

key-files:
  created: []
  modified:
    - "analyzer-reports/phase-68-summary.json - overwrote the stale single-TEST-07 working-tree copy with the honest 7-row rerun roll-up (6 fresh Release verdicts = Fail, TEST-07 = INDETERMINATE)"

key-decisions:
  - "D-04 verdict SHIFT treated as a FINDING, NOT silently re-baselined and NOT forced green (per plan T-78-08 / runbook). The gate is recorded as FAILED-FINDING."
  - "Authoritative verdicts are the FRESH bin/Release/{id}.json reports written by THIS run's analyzer (-c Release), NOT the harness exit codes: the harness exit code for TEST-01 was contaminated by a stale 2026-06-18 bin/Debug/TEST-01.json shadowing the fresh Release report (Debug-shadow variant of the documented stale-report trap)."
  - "Reseed order trap ([[rebuild-sourcehash-reseed-order]]) hit and resolved: phase-65-reset heal-wait fails after a Phase-77 rebuild because the new SourceHash has no processor row; unblocked by manual graph-DELETE -> seed (registers the row) -> processor heartbeats -> liveness reconverges -> 204."

patterns-established:
  - "Live-gate authority rule: trust the FRESH -c Release {id}.json over the harness/roll-up exit code whenever a stale Debug or prior-run Release copy can shadow it under Get-ChildItem -Recurse | Select -First 1."

requirements-completed: []

# Metrics
duration: ~3.5h (reseed + 7-scenario live sweep)
completed: 2026-07-17
---

# Phase 78 Plan 04: Terminal live gate (D-04) — Summary (BLOCKING FINDING)

**The D-04 live gate was executed end-to-end (mandatory SourceHash reseed, then the 7-scenario `phase-68-sweep.ps1` live on Docker). It does NOT reproduce the committed-HEAD baseline. On fresh live data the reworked analyzer flips EVERY scenario PASS -> FAIL — including the no-fault baseline TEST-01 — by flagging 100% of executions as effect-once (duplicate) violations (`Duplicates == StartedRuns` for all six fresh scenarios, `Missing == 0`, `InFlightLoss == 0`). This is a genuine verdict SHIFT, surfaced as a FINDING per the runbook: NOT silently re-baselined, NOT forced green. Root cause (high confidence): against Phase 77's uniform execution-scope logging, each hop now emits multiple StepId-scoped framework records, and the reworked structural step query counts each as a distinct step execution, so the effect-once check trips on every hop of every run with the value axis fully dark.**

## Runbook executed

Reseed-first per [[rebuild-sourcehash-reseed-order]], then the sweep:

1. `docker compose build` — images current (cached from Task 1's rebuild).
2. `scripts/phase-65-up.ps1` — 10 service types healthy.
3. `scripts/phase-65-reset.ps1` — **FAILED at STEP 2 heal-wait** (liveness did not reconverge in 60s). Processor logs: `Processor row not yet registered for hash 5ff954f2...; retrying in 00:00:30`. This is the documented reseed trap: Phase 77 touched `BaseProcessor.Core`, so the new SourceHash has no processor row and the processor will not heartbeat liveness until seeded.
4. **Deviation (Rule 3 — unblock):** manual FK-safe graph-DELETE (the same SQL as reset STEP 3), then seed `dotnet test ... ~FanOutSeeder` (registers the processor row for hash `5ff954f2`). Liveness reconverged in ~2s (2 replicas under proc `13f96a26...`). The seeded `processors` row persists in the postgres volume, so every subsequent harness reset heal-wait passed throughout the sweep.
5. Resolved `v8-fanout-proof` wfId, `POST /orchestration/start` -> **204** (activation gate confirmed).
6. `pwsh -File scripts/phase-68-sweep.ps1` — run-all, no fail-fast.

The sweep completed TEST-01..06 and was killed by the environment during TEST-07's STEP H analyze (before it wrote a fresh report or emitted an exit). The stack was torn down cleanly afterward (`docker compose down`, volumes kept).

## Rerun verdict table vs HEAD baseline

Authoritative = the FRESH `bin/Release/{id}.json` this run's analyzer wrote.

| Scenario | HEAD baseline | This-run FRESH Release verdict | Started/Complete | Missing | InFlightLoss | Duplicates | Harness exit (as surfaced) |
|----------|---------------|-------------------------------|------------------|---------|--------------|------------|-----------------------------|
| TEST-01 (no-fault) | Pass 11/11 | **Fail** | 19/19 | 0 | 0 | **19** | 0 (FALSE PASS — stale Debug shadow) |
| TEST-02 (processor) | Pass 10/10 | **Fail** | 17/17 | 0 | 0 | **17** | 1 (FAIL) |
| TEST-03 (orchestrator) | Pass 9/9 | **Fail** | 17/17 | 0 | 0 | **17** | 1 (FAIL) |
| TEST-04 (keeper) | Pass 10/10 | **Fail** | 22/22 | 0 | 0 | **22** | 1 (FAIL) |
| TEST-05 (redis) | Pass 8/8 | **Fail** | 25/25 | 0 | 0 | **25** | 1 (FAIL) |
| TEST-06 (rabbitmq) | Pass 8/8 | **Fail** | 19/19 | 0 | 0 | **19** | 1 (FAIL) |
| TEST-07 (redis+rabbitmq) | Pass 8/8 | **INDETERMINATE** | — | — | — | — | — (analyze killed; on-disk copy is a stale 07-16 Pass 24/24) |

**Signature:** in every fresh scenario `Duplicates == StartedRuns == CompleteRuns` and `Missing == InFlightLoss == 0`. The completeness/loss axes are perfectly clean; the ONLY failing axis is effect-once. A no-fault baseline (TEST-01) with zero crashes cannot legitimately have 100% duplicate runs — this is systematic, not live redelivery variance.

**Per-execution evidence (TEST-01, no-fault):** one execution has 30 total StepId records over 10 distinct stepIds — each hop's stepId appears 3-4x. There was no crash and no redelivery, so the analyzer is counting multiple framework log records per hop as separate step executions.

## Confirmation the value axis was dark

The value oracle was deleted end-to-end in Plan 03 (`ExpectedHopOffset`, `valueOracleHopSet`, the `DistinctLabels` label-fallback, `RunTrace.DistinctLabels`). The failures here are produced by the STRUCTURAL effect-once check on ES stepId cardinality with no value-oracle contribution — so the rework's "verdict reconstructs from framework ES logs alone" claim is literally exercised. The problem is that the structural reconstruction is now WRONG on live data: it over-counts per-hop framework records.

## Root cause (high confidence, for the follow-up fix)

Phase 77 made execution-scope (Tier-1 ids incl. `StepId`) uniform across base-processor/orchestrator/keeper send+consume framework logs, and had the keeper open the execution scope on each recovery consume. Consequently each hop now emits MULTIPLE `StepId`-scoped framework records (dispatch-send, dispatch-consume, result-send, result-consume, keeper recovery-consume). Phase 78's analyzer rework (D-01 entry-marker self-exclusion, D-02 keeper `must_not`, D-03 value-oracle delete) does NOT collapse these multiple per-hop records to one step execution, so `BuildStepSearchBody`'s cohort counts each as a distinct step -> the effect-once/`Duplicates` detector trips on every hop of every run. The hermetic suite passed because its `RunTrace.FromStepIds` facts feed exactly one record per stepId — a shape the live pipeline no longer produces. The fix belongs in the step query / cohort de-duplication (collapse the multiple scoped framework records per `{executionId, stepId}` to a single step execution, e.g. select only the canonical record kind), NOT in re-baselining.

## Deviations from Plan

**1. [Rule 3 - Blocking] Manual reseed to clear the heal-wait trap**
- **Found during:** Reseed Step 3 (`phase-65-reset.ps1`).
- **Issue:** reset STEP 2 heal-wait aborts after a Phase-77 rebuild (new SourceHash has no processor row; processor withholds liveness).
- **Fix:** manual FK-safe graph-DELETE -> seed -> processor registers -> liveness reconverges -> 204. This is the documented [[rebuild-sourcehash-reseed-order]] procedure. Seeded row persists in the postgres volume so the sweep's per-scenario resets all passed.
- **Files modified:** none (live DB/graph state only).
- **Commit:** none (runtime state).

## Known Traps hit

- **Reseed heal-wait trap ([[rebuild-sourcehash-reseed-order]])** — hit and resolved as above.
- **Debug-shadow stale-report trap (deeper variant of [[phase-68-sweep-stale-report-read]])** — a stale 2026-06-18 `bin/Debug/TEST-01.json` (Pass) shadowed the fresh Release Fail under `Get-ChildItem -Recurse | Select -First 1`, so the HARNESS ITSELF (not just the roll-up) surfaced a FALSE PASS exit 0 for TEST-01. The documented guidance "trust the harness exit code" is INVALID here because the harness's own read was stale. Only TEST-01 had a Debug copy; TEST-02..06 read fresh Release and reported FAIL correctly.
- **TEST-07 killed mid-analyze** — the on-disk `bin/Release/TEST-07.json` is a stale 07-16 Pass 24/24 and must not be read as this run's result. TEST-07 is INDETERMINATE; it does not change the finding.
- **TEST-01 cold-ES INCONCLUSIVE flake** — did NOT occur (TEST-01 produced a definite Fail verdict, not Inconclusive), so no deliberate warm re-run was applicable.

## Outcome

**D-04 acceptance (all 7 reproduce HEAD baseline PASS) is NOT met.** The live gate ran to a conclusive result and surfaced a systematic regression in the reworked structural effect-once reconstruction against Phase 77's logging model. Recorded as a FINDING/blocker for a follow-up fix plan; not re-baselined, not forced green. Requirement D-04 remains OPEN.

## Self-Check: PASSED

- analyzer-reports/phase-68-summary.json — FOUND (rerun roll-up written; 7 rows)
- .planning/phases/78-.../78-04-SUMMARY.md — FOUND (this file)
- Fresh Release reports TEST-01..06 — FOUND (mtime 2026-07-17 00:42..01:25)
- No commits claimed for per-task work (live-state runbook only); the metadata commit records this SUMMARY + roll-up + STATE blocker.
