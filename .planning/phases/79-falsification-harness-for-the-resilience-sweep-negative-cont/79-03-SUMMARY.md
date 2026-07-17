---
phase: 79-falsification-harness-for-the-resilience-sweep-negative-cont
plan: 03
subsystem: testing
tags: [powershell, resilience-harness, negative-control, falsification, keeper-reinject, docker-compose]

# Dependency graph
requires:
  - phase: 79-01
    provides: "ReinjectConsumer static one-shot latch gating ep.Send behind KEEPER_DEFEAT_REINJECT; compose.yaml keeper env KEEPER_DEFEAT_REINJECT default-off"
provides:
  - "FALSIFY-01 out-of-band scenario row in phase-67-harness.ps1 $Scenarios (faultType=inject-recovery-loss)"
  - "inject-recovery-loss export branch: KEEPER_DEFEAT_REINJECT=1 + K_EXECUTIONS=1 armed before STEP A bring-up"
  - "STEP F.2 dedicated no-crash branch: --scale keeper=1 + keeperCount -ne 1 abort guard + RECOVERY_UTC pinned at window start"
  - "STEP-H finally teardown clears Env:KEEPER_DEFEAT_REINJECT"
affects: [79-04, 79-05]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Env-gated product seam harness half: export before compose-up + clear in finally (mirrors PROCESSOR_STEP_DELAY_MS)"
    - "Single-replica scale + count-guard to make a process-global static latch defeat exactly one event"

key-files:
  created:
    - ".planning/phases/79-falsification-harness-for-the-resilience-sweep-negative-cont/79-03-SUMMARY.md"
  modified:
    - "scripts/phase-67-harness.ps1"

key-decisions:
  - "inject-recovery-loss takes a dedicated no-crash STEP-F branch (targetContainers empty); never enters the crash sequencer or the queue-depth poll"
  - "keeper scaled to 1 replica via `docker compose up -d --no-recreate --scale keeper=1` with a keeperCount -ne 1 abort (exit 60) so the process-global latch defeats at most one reinject; combined with K_EXECUTIONS=1"
  - "RECOVERY_UTC pinned at window start so the defeated reinject registers as a post-recovery BINDING miss (no redis-wipe in-flight tolerance)"

patterns-established:
  - "Negative-control scenario kept out of the default TEST-01..07 capstone, run only by explicit id (TEST-08/09/10 convention)"

requirements-completed: []

# Metrics
duration: 12min
completed: 2026-07-17
---

# Phase 79 Plan 03: Wire FALSIFY-01 into phase-67-harness Summary

**FALSIFY-01 negative-control scenario wired into phase-67-harness.ps1 — arms the keeper KEEPER_DEFEAT_REINJECT seam + K_EXECUTIONS=1 before bring-up, runs a no-crash observe path with the keeper scaled to a single replica (count-guarded) so exactly one reinject is defeated, pins RECOVERY_UTC at window start for a post-recovery binding miss, and tears the seam down in the STEP-H finally.**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-07-17T10:00Z (approx)
- **Completed:** 2026-07-17
- **Tasks:** 1
- **Files modified:** 1 (harness), 1 created (summary)

## Accomplishments
- Added the `FALSIFY-01` row to `$Scenarios` (`faultType = 'inject-recovery-loss'`, `targetContainers = @()`, `injectAfterNFires = 0`, `dwellSeconds = 0`), following the TEST-08/09/10 negative-path "not in the default sweep" convention.
- Added the export branch that arms `KEEPER_DEFEAT_REINJECT=1` + `K_EXECUTIONS=1` BEFORE STEP A, so `docker compose up` bakes the seam into the keeper container (`${KEEPER_DEFEAT_REINJECT:-0}`).
- Restructured the STEP-F fault guard: `inject-recovery-loss` now takes a dedicated FIRST no-crash branch that scales the keeper to a single replica (`--scale keeper=1`) with a `keeperCount -ne 1` abort guard, then pins `RECOVERY_UTC` at window start. The existing crash/inject block was converted from `if` to `elseif` byte-identically; the no-fault `else` baseline is unchanged.
- Added `Env:KEEPER_DEFEAT_REINJECT` to the STEP-H `finally` `Remove-Item` teardown list.

## Task Commits

Each task was committed atomically:

1. **Task 1: Add FALSIFY-01 row + inject-recovery-loss export/scale/no-crash branch + teardown** - `c537164` (feat)

**Plan metadata:** (final docs commit follows)

## Files Created/Modified
- `scripts/phase-67-harness.ps1` - FALSIFY-01 scenario row + inject-recovery-loss env export branch + STEP-F no-crash/scale-to-1 branch + finally teardown addition. TEST-01..07 rows byte-unchanged.

## Decisions Made
- None beyond the plan — all four edits implemented exactly as specified. The keeper scale-to-1 + `keeperCount -ne 1` guard resolves the `deploy.replicas: 2` per-process-latch finding mandated by the plan/upstream 79-01 artifacts.

## Deviations from Plan

None - plan executed exactly as written. (The only micro-adjustment: the RECOVERY_UTC-pin comment references "the negative-path idiom at F.3" rather than a hard `:385` line number, since line numbers shift after the insertions — semantics identical.)

## Issues Encountered
None. The script parses clean (PowerShell AST parse, zero errors) and all acceptance-criteria greps match.

## Verification

- **AST parse:** `pwsh` `Parser::ParseFile` → `PARSE_CLEAN` (zero syntax errors).
- **Grep acceptance criteria (all matched):**
  - `'FALSIFY-01'` row present with `faultType = 'inject-recovery-loss'` and `targetContainers = @()` (line 107)
  - `faultType -eq 'inject-recovery-loss'` matches twice (export branch line 133, STEP-F branch line 332)
  - `KEEPER_DEFEAT_REINJECT = '1'` (line 134) + `K_EXECUTIONS = '1'` (line 135)
  - `--scale keeper=1` (line 339) + `keeperCount -ne 1` abort guard (line 343)
  - `$recoveryUtc = $windowStart` present in the inject-recovery-loss branch (line 347) in addition to the existing negative-path occurrence (line 414)
  - STEP-H finally `Remove-Item` list includes `Env:KEEPER_DEFEAT_REINJECT` (line 560)
- **Scope:** `git diff --stat` = 31 insertions / 2 deletions in the single harness file; `phase-68-sweep.ps1` untouched; no file deletions in the commit.
- **Not run here (by design):** the live Docker stack proof is Plan 79-05's job. This plan only WIRES the scenario; behavioral verdict-flip is exercised live downstream.

## Next Phase Readiness
- FALSIFY-01 is a self-contained, out-of-band harness scenario ready for Plan 79-04 (the standalone `phase-79-falsify.ps1` driver invoking `phase-68-sweep.ps1 -ScenarioIds FALSIFY-01` with the inverted assertion) and Plan 79-05 (the live gate-teeth proof).
- No blockers.

## Self-Check: PASSED

- FOUND: `.planning/phases/79-falsification-harness-for-the-resilience-sweep-negative-cont/79-03-SUMMARY.md`
- FOUND: commit `c537164`
- FOUND: `scripts/phase-67-harness.ps1`

---
*Phase: 79-falsification-harness-for-the-resilience-sweep-negative-cont*
*Completed: 2026-07-17*
