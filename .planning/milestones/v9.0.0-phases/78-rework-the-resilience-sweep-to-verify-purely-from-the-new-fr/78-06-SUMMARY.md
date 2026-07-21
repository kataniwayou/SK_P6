---
phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
plan: 06
subsystem: testing
tags: [analyzer, resilience-sweep, live-gate, D-04, gap-closure, structural-cohort, effect-once, duplicates, reseed, capstone, baseline-oracle]

# Dependency graph
requires:
  - phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
    plan: 05
    provides: "D-04 canonical-record fix (StructuralCohort selects ONLY the 'hop executed' consume record as the observed did-run hop) + hermetic StructuralCohortFacts pinning collapse->Pass and genuine-redelivery->Fail"
provides:
  - "D-04 TERMINAL LIVE GATE re-run AFTER the 78-05 fix — reseed-first + 7-scenario phase-68-sweep run live on Docker; result is a CLEAN 7/7 PASS (NOT a finding)"
  - "All 7 scenarios (TEST-01..07) reproduce their genuine da91d32 all-PASS baseline verdict; the 78-04 over-count regression (Duplicates == StartedRuns) is GONE on real Phase-77 data — every scenario shows Duplicates == 0, including the no-fault TEST-01"
  - "Fresh analyzer-reports/phase-68-summary.json roll-up (7/7 PASS) — restores the tracked baseline file that commit 5096dcd had clobbered with the 78-04 failed-finding rerun"
  - "D-04 requirement CLOSED: the resilience verdict reconstructs PURELY AND CORRECTLY from framework ES logs alone (canonical-record structural completeness + ANL-03 redundancy + the Prometheus metric gate), value-oracle axis dark"
affects: [78, resilience-sweep, live-gate, D-04, pass-fail-engine, structural-cohort, milestone-close]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Live re-gate authority: the FRESH bin/Release/{id}.json this run's -c Release analyzer wrote is authoritative over the harness exit code and roll-up row (Debug-shadow trap) — verified this run wrote ONE report per scenario, all mtime today, no Debug shadow"
    - "Baseline-oracle integrity (T-78-13): gate against the genuine all-PASS commit (da91d32), NOT git-show HEAD when a prior failed-finding rerun (5096dcd) has clobbered the tracked baseline file"

key-files:
  created: []
  modified:
    - "analyzer-reports/phase-68-summary.json - regenerated from the 7 fresh Release reports (7/7 PASS); restores the tracked baseline to an all-PASS state, correcting the 5096dcd clobber"

key-decisions:
  - "BASELINE-ORACLE CORRECTION: the plan runbook said gate against `git show HEAD:analyzer-reports/phase-68-summary.json`, but at the current HEAD that file is the 78-04 FAILED rerun (commit 5096dcd overwrote it — verified 0 Pass / 6 Fail + 1 INDETERMINATE at HEAD). The genuine all-PASS D-04 oracle is `git show da91d32:analyzer-reports/phase-68-summary.json` = 7/7 PASS. EVERY fresh verdict was gated against da91d32, never HEAD (gating against HEAD would be doubly dangerous per T-78-13)."
  - "D-04 gates on VERDICT reproduction, not count reproduction: fresh startedRuns (19/17/18/17/23/14/25) differ from the da91d32 counts (11/10/9/10/8/8/8) because each 300s window fires a run-dependent number of executions. All 7 are Pass with startedRuns == completeRuns, Missing == 0, Duplicates == 0 — a legitimate reproduction."
  - "TEST-07 re-analyze (not full re-run): the background sweep process was killed during TEST-07 STEP H analyze (same trap as 78-04), but TEST-07's fault window had ALREADY completed and its data was in ES ([07:29:21.70Z, 07:36:42.83Z], recovery 07:31:12.44Z). Rather than a stale copy or a fresh full re-run, the analyzer was re-invoked against that exact completed window (the genuine redis+rabbitmq fault run) → fresh Pass 25/25 report. No stale copy read."

patterns-established:
  - "Reseed-FIRST defeats the STEP B heal-wait trap for the whole sweep: manual graph-DELETE -> seed (registers the current-SourceHash processors row) -> confirm 204 BEFORE launching the sweep, so the first scenario's phase-65-reset heal-wait finds a live liveness key and the persisted processors row carries every subsequent per-scenario reset."

requirements-completed: [D-04]

# Metrics
duration: ~2h (reseed + 7-scenario live sweep + TEST-07 re-analyze)
completed: 2026-07-17
---

# Phase 78 Plan 06: D-04 terminal live re-gate (post-fix) — Summary (7/7 PASS — D-04 CLOSED)

**The D-04 terminal live gate was re-run end-to-end AFTER the 78-05 canonical-record fix (mandatory SourceHash reseed-first, then the 7-scenario `phase-68-sweep.ps1` live on Docker), and it now reproduces the genuine all-PASS baseline: 7/7 PASS. Every scenario shows `Duplicates == 0` — including the no-fault TEST-01, which in 78-04 falsely flagged 19/19 duplicate. The 78-04 over-count regression signature (`Duplicates == StartedRuns` on every hop of every run) is completely GONE on real Phase-77 live data. The verdicts reconstruct PURELY AND CORRECTLY from framework ES logs alone (canonical `"hop executed"` structural completeness + ANL-03 redundancy + the Prometheus metric gate) with the value-oracle axis fully dark. D-04 is CLOSED.**

## Baseline-oracle correction (recorded explicitly — T-78-13)

The plan's runbook instructed gating against `git show HEAD:analyzer-reports/phase-68-summary.json`. **That is wrong at the current HEAD.** Commit `5096dcd` (the 78-04 finding) overwrote the tracked baseline file with the 78-04 FAILED rerun, so `git show HEAD:...` returns **0 Pass / 6 Fail + 1 INDETERMINATE** (verified). The genuine all-PASS D-04 oracle is **`git show da91d32:analyzer-reports/phase-68-summary.json` = 7/7 PASS** (commit `da91d32`, "capstone 7/7"). Every fresh scenario verdict this run was gated against **da91d32**, never HEAD. Gating against the clobbered HEAD would be doubly dangerous: a correct post-fix PASS would look like a shift, and a still-broken FAIL would falsely "reproduce HEAD" and silently close D-04.

## Runbook executed (reseed-FIRST per [[rebuild-sourcehash-reseed-order]])

1. `docker compose build` — all 4 images built (near-fully cached; 78-05 changed only test code, so the container SourceHash is the Phase-77 build). Exit 0.
2. `docker compose up -d --force-recreate` + `scripts/phase-65-up.ps1` — 10 service types healthy.
3. **Reseed FIRST (defeats the heal-wait trap for the whole sweep):** manual FK-safe graph-DELETE (`step_next_steps`/`workflow_assignments`/`workflow_entry_steps`/`assignments`/`workflows`/`steps`, single transaction) → `dotnet test ~FanOutSeeder -c Release` (self-verified, registers the processors row for the current SourceHash) → confirmed **2 per-instance liveness keys** present and the **POST /orchestration/start → 204** activation gate. The persisted `processors` row then carried every per-scenario reset.
4. `pwsh -File scripts/phase-68-sweep.ps1` — run-all, no fail-fast. The first scenario's STEP B `phase-65-reset` heal-wait PASSED (liveness reconverged, 1 key present) — the 78-04 trap is pre-empted by the reseed.
5. Per scenario, read the FRESH `bin/Release/{id}.json` and compared its verdict to `git show da91d32:...`.
6. `docker compose down` (kept volumes + images) after the run.

## Rerun verdict table vs the da91d32 all-PASS baseline

Authoritative = the FRESH `bin/Release/{id}.json` this run's `-c Release` analyzer wrote (all mtime 2026-07-17, one report per scenario, no Debug shadow).

| Scenario | da91d32 baseline | This-run FRESH Release | Missing | Duplicates | Reproduces baseline? |
|----------|------------------|------------------------|---------|------------|----------------------|
| TEST-01 (no-fault)        | Pass 11/11 | **Pass 19/19** | 0 | **0** | ✓ |
| TEST-02 (processor)       | Pass 10/10 | **Pass 17/17** | 0 | **0** | ✓ |
| TEST-03 (orchestrator)    | Pass 9/9   | **Pass 18/18** | 0 | **0** | ✓ |
| TEST-04 (keeper)          | Pass 10/10 | **Pass 17/17** | 0 | **0** | ✓ |
| TEST-05 (redis)           | Pass 8/8   | **Pass 23/23** | 0 | **0** | ✓ |
| TEST-06 (rabbitmq)        | Pass 8/8   | **Pass 14/14** | 0 | **0** | ✓ |
| TEST-07 (redis+rabbitmq)  | Pass 8/8   | **Pass 25/25** | 0 | **0** | ✓ |

All 7 reproduce their da91d32 PASS. Counts differ (run-dependent fire count per 300s window) but D-04 gates on verdict reproduction, not count reproduction; every scenario has `startedRuns == completeRuns`, `Missing == 0`, `Duplicates == 0`.

## The 78-04 over-count signature is GONE

The 78-04 finding: every fresh scenario had `Duplicates == StartedRuns == CompleteRuns` (e.g. the no-fault TEST-01 flagged 19/19 duplicate) because the old `hasMessageId` discriminator counted the `"hop executed"` + `"result sent"` + `"fan-out"` records (3-4× per hop post-Phase-77) as distinct step executions. This run, after the 78-05 canonical-record fix, **the no-fault TEST-01 shows `Duplicates == 0`** (verified directly on the fresh report: Verdict Pass, 19 started / 19 complete / 0 missing / 0 duplicates), and every fault scenario likewise shows `Duplicates == 0`. The `Duplicates == StartedRuns` signature is absent on all 7. The collapse-by-canonical-record-KIND selection preserves genuine effect-once detection (proven hermetically in 78-05 `StructuralCohortFacts`) while removing the framework send/consume multiplicity over-count on live data.

## Confirmation the value axis was dark

The value oracle was deleted end-to-end in Plan 03 (`ExpectedHopOffset`, `valueOracleHopSet`, the `DistinctLabels` label-fallback, `RunTrace.DistinctLabels`). These 7 PASS verdicts are produced by the STRUCTURAL canonical-record completeness check + ANL-03 redundancy resolver + the Prometheus trigger-count metric gate, with NO StepLabel/Produced value evidence contributing — so the rework's "verdict reconstructs from framework ES logs alone" claim is literally exercised, and this run proves the structural reconstruction is now both PURE and CORRECT on live data.

## Deviations from Plan

**1. [Rule 3 - Blocking] Baseline-oracle correction (da91d32 vs the clobbered HEAD)**
- **Found during:** pre-gate oracle capture (Task 1 residue / Task 2 gating).
- **Issue:** the runbook's `git show HEAD:...` oracle is the 78-04 failed-finding rerun (commit 5096dcd clobbered the tracked file); gating against it would be doubly dangerous (T-78-13).
- **Fix:** gated every verdict against `git show da91d32:...` (7/7 PASS, the genuine capstone baseline). Recorded explicitly above.
- **Files modified:** none for the fix itself; the roll-up write (below) restores the tracked file to all-PASS.
- **Commit:** metadata commit (this SUMMARY + roll-up).

**2. [Rule 3 - Blocking] TEST-07 analyze re-invoked against its completed window**
- **Found during:** end of sweep (TEST-07 STEP H).
- **Issue:** the background sweep process was killed during TEST-07's analyze (same trap 78-04 hit), before STEP H wrote a report — no TEST-07.json existed (Task 1 had cleared the stale copy, so nothing could shadow-mislead).
- **Fix:** TEST-07's fault window had already completed with its data in ES ([07:29:21.70Z, 07:36:42.83Z], recovery 07:31:12.44Z); re-invoked the analyzer (`--filter-method *Analyze_Window_Yields_Pass*`, `-c Release`) with those exact window env seams → fresh Pass 25/25 report from the genuine redis+rabbitmq fault run. No stale copy read; no re-baseline.
- **Files modified:** none (produced the fresh TEST-07.json report).
- **Commit:** none (runtime analyze).

## Known Traps handled

- **Reseed heal-wait trap ([[rebuild-sourcehash-reseed-order]])** — pre-empted by the reseed-first runbook; the first scenario's reset heal-wait passed cleanly (never aborted this run).
- **Debug-shadow stale-report trap** — Task 1 cleared stale bin/Debug + bin/Release scenario reports; re-verified none reappeared. This run wrote exactly ONE report per scenario under bin/Release, all mtime today — no Debug copy existed to shadow any fresh Release report. The harness exit codes and the fresh reports agreed on all 7.
- **TEST-07 killed mid-analyze** — handled by the completed-window re-analyze above (Deviation 2); TEST-07 produced a FRESH report this run.
- **TEST-01 cold-ES INCONCLUSIVE flake** — did NOT occur; TEST-01 produced a definite Pass verdict, so no deliberate warm re-run was applicable.

## Outcome

**D-04 acceptance is MET: all 7 scenarios reproduce their genuine (da91d32) all-PASS baseline, the over-count is gone (TEST-01 Duplicates == 0), and the value axis is dark.** No silent re-baseline; the fresh roll-up was regenerated from the 7 fresh Release reports and, being 7/7 PASS, naturally restores the tracked `analyzer-reports/phase-68-summary.json` to an all-PASS state (correcting the 5096dcd clobber). Requirement **D-04 is CLOSED** — the resilience verdict reconstructs purely AND correctly from framework ES logs.

## Self-Check: PASSED

- analyzer-reports/phase-68-summary.json — FOUND (7/7 PASS roll-up regenerated from fresh reports)
- Fresh Release reports TEST-01..07 — FOUND (all mtime 2026-07-17 09:45..10:47, one per scenario, no Debug shadow)
- da91d32 baseline oracle = 7 Pass; HEAD (clobbered) = 0 Pass — verified via git show
- .planning/phases/78-.../78-06-SUMMARY.md — FOUND (this file)
- No per-task code commits (live-state runbook only); the metadata commit records this SUMMARY + roll-up + STATE + ROADMAP.
</content>
</invoke>
