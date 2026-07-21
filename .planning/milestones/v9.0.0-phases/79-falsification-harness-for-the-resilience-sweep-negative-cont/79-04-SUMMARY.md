---
phase: 79-falsification-harness-for-the-resilience-sweep-negative-cont
plan: 04
subsystem: testing
tags: [powershell, negative-control, falsification, resilience-sweep, gate-teeth, exit-codes]

# Dependency graph
requires:
  - phase: 79-01
    provides: keeper KEEPER_DEFEAT_REINJECT env seam + compose plumbing (the True-Positive loss source)
  - phase: 79-03
    provides: FALSIFY-01 scenario row in phase-67-harness.ps1 (faultType inject-recovery-loss, K_EXECUTIONS=1)
provides:
  - "scripts/phase-79-falsify.ps1 — standalone inverted-assertion negative-control driver"
  - "Fail-closed meta-test asserting the resilience-sweep gate flips FALSIFY-01 to VERDICT_FAIL for the recoverable-but-lost binding-miss reason"
affects: [79-05, resilience-sweep, gate-teeth, live-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Inverted-assertion negative-control driver: own exit 0 == the gate correctly went RED for the right reason"
    - "Exact-1 harness-exit assertion (never merely non-zero) so INCONCLUSIVE/infra-abort can't masquerade as a passing control"
    - "Read-artifacts-never-re-score: reads phase-68-summary.json row + freshest per-scenario analyzer report"

key-files:
  created:
    - scripts/phase-79-falsify.ps1
  modified: []

key-decisions:
  - "Driver invokes phase-68-sweep.ps1 -ScenarioIds FALSIFY-01 once — the single sweep run surfaces BOTH the harness-exit level (summary row) and the sweep-summary level; no direct harness re-run"
  - "Assert harnessExit == 1 EXACTLY (D-03 #1); harnessExit 2 -> exit 2, 64 -> exit 64, other non-{0,1} -> passthrough BEFORE the pass/fail decision"
  - "Right-reason evidence (D-03 #2): Missing >= 1 AND MissingDetail contains both 'recoverable-but-lost' and 'binding miss'"
  - "Freshest FALSIFY-01.json by LastWriteTime -Descending (T-79-09 stale-report guard)"

patterns-established:
  - "phase-79-falsify.ps1 mirrors phase-68-sweep.ps1 skeleton (ErrorActionPreference Stop, StrictMode, repoRoot Push/Pop, Write-Phase, dot-sourced exit-code-resolution.ps1) with the assertion INVERTED"

requirements-completed: []

# Metrics
duration: 3min
completed: 2026-07-17
---

# Phase 79 Plan 04: Inverted-Assertion Negative-Control Driver Summary

**scripts/phase-79-falsify.ps1 — a fail-closed meta-test that drives phase-68-sweep.ps1 -ScenarioIds FALSIFY-01 and asserts the gate flipped it to VERDICT_FAIL for the recoverable-but-lost binding-miss reason (harnessExit==1 exactly, Missing>=1, MissingDetail names both substrings, sweep non-zero); own exit 0 == the gate has teeth.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-07-17T10:06:43Z
- **Completed:** 2026-07-17T10:08:04Z
- **Tasks:** 1
- **Files modified:** 1 (created)

## Accomplishments
- Authored the standalone negative-control driver with the INVERTED assertion contract encoding D-03 (right-reason) and D-04 (driven-through-the-sweep, dual-level) exactly.
- Encoded the fail-closed exit-code table as a header comment mirroring phase-67-harness.ps1:31-44 with the meaning flipped (own exit 0 == gate correctly RED).
- Reused the shared `scripts/lib/exit-code-resolution.ps1` (dot-sourced) and read-only artifact tabulation — never re-scores Missing/Duplicates.
- Wired the T-79-09 stale-report guard (freshest FALSIFY-01.json by LastWriteTime) and the T-79-10 exact-1 gate so INCONCLUSIVE (2) / infra abort (10-70/64) can never masquerade as a passing negative control.

## Task Commits

Each task was committed atomically:

1. **Task 1: Author scripts/phase-79-falsify.ps1 (inverted-assertion negative-control driver)** - `92b94ad` (feat)

**Plan metadata:** (this commit — docs: complete plan)

## Files Created/Modified
- `scripts/phase-79-falsify.ps1` - Standalone falsification driver: invokes `phase-68-sweep.ps1 -ScenarioIds FALSIFY-01`, reads the summary row + freshest analyzer report, and holds the fail-closed inverted assertion (harnessExit==1 AND sweep non-zero AND verdict=='Fail'/VERDICT_FAIL AND Missing>=1 AND MissingDetail names recoverable-but-lost + binding miss), with INCONCLUSIVE/BAD_ARG/infra passthroughs.

## Decisions Made
None beyond those pinned in the plan/CONTEXT — followed the plan's step-by-step body verbatim. Env-var/id shapes were fixed upstream (79-01/79-03).

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None. The PowerShell AST parse returned zero errors on the first write; all 10 discrete grep acceptance criteria matched.

## Verification
- **AST parse:** `[System.Management.Automation.Language.Parser]::ParseFile(...)` → zero errors (`PARSE_OK`).
- **Combined grep gate (plan `<verify>`):** `DRIVER_OK` (≥5 required patterns present).
- **Discrete acceptance criteria:** all 10 matched — `phase-68-sweep.ps1`, `'FALSIFY-01'`, `harnessExit -eq 1`, `recoverable-but-lost`, `binding miss`, `verdict … -eq 'Fail'`, `VERDICT_FAIL`, `harnessExit -eq 2` + `exit 2`, `exit-code-resolution.ps1`.
- **No live stack run** — Docker/live behavioral proof (the driver actually exiting 0 against a real defeated reinject) is Plan 79-05's concern, per the plan's verification scope.
- **Post-commit deletion check:** no files deleted.

## Known Stubs
None. The driver is a complete standalone script; its only external dependencies are the already-shipped `phase-68-sweep.ps1`, `phase-67-harness.ps1` FALSIFY-01 row, keeper seam, and `exit-code-resolution.ps1`.

## Next Phase Readiness
- Plan 79-05 (the live behavioral proof) can now execute the reseed-first runbook, rebuild the keeper image (product source changed in 79-01), run `scripts/phase-79-falsify.ps1`, and confirm it exits 0 (gate has teeth) against a real defeated reinject.
- No blockers introduced by this plan (script is Docker-gated live-only via the sweep it drives; inert in every non-live run).

## Self-Check: PASSED

- FOUND: scripts/phase-79-falsify.ps1
- FOUND: .planning/phases/79-.../79-04-SUMMARY.md
- FOUND: commit 92b94ad

---
*Phase: 79-falsification-harness-for-the-resilience-sweep-negative-cont*
*Completed: 2026-07-17*
