---
phase: 81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o
plan: 03
subsystem: infra
tags: [powershell, kubectl, sweep, roll-up, fault-recovery-sweep, capstone]

# Dependency graph
requires:
  - phase: 81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o
    provides: "phase-80-harness.ps1 generalized with -ScenarioId + -SkipBringUp (plan 81-01) and phase-80-reset.ps1 rabbitmq drain (plan 81-02)"
  - phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
    provides: "phase-80-build.ps1 (4-image :local build) + phase-80-up.ps1 (apply + rollout + 8 port-forwards + readiness gate) + lib/exit-code-resolution.ps1"
provides:
  - "scripts/phase-81-sweep.ps1 — the k8s capstone sweep: one-time build+bring-up, per-scenario child harness loop (-SkipBringUp, no fail-fast), roll-up + analyzer-reports/phase-81-summary.json"
  - "single operator command for the k8s 7/7 capstone proof (exit 0 IFF all selected scenarios PASS)"
affects: [phase-80-harness, phase-80-up, phase-80-build, k8s-fault-recovery-sweep]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "sweep-owned shared-stack lifecycle: bring up ONCE (STEP 0) → loop N scenarios via child -SkipBringUp → port-forward teardown ONCE (STEP Z)"
    - "child-process exit-code seam: pwsh -File harness surfaces exit N as $LASTEXITCODE without terminating the loop (no fail-fast)"

key-files:
  created:
    - "scripts/phase-81-sweep.ps1 - k8s capstone sweep driver (loop + classification + roll-up + one-time bring-up/teardown)"
  modified: []

key-decisions:
  - "Option A (sweep-owned bring-up + harness -SkipBringUp) chosen for D-03 — keeps phase-80-up.ps1 UNCHANGED, gives an explicit bring-up-once/run-N contract; rejected Option B (idempotent self-guard) which would require making the monolithic up script individually skippable (out of scope)"
  - "Classification body ported byte-identical: Resolve-SweepClass + report discovery + tabulation + roll-up — the wrapper only READS the analyzer's Verdict/Missing/Duplicates/StartedRuns/CompleteRuns, never re-scores (SPEC-5)"
  - "param default is exactly the 7 capstone ids TEST-01..07 — never TEST-08/09/10 or FALSIFY-01/02 (SPEC-8); INCONCLUSIVE (exit 2) is operator-re-runnable, never auto-retried (SPEC-6)"
  - "Reworded a doc-comment analog reference from 'phase-68-sweep.ps1' to 'the compose capstone sweep' to satisfy the plan's own zero-phase-68-references acceptance criterion"

patterns-established:
  - "STEP Z teardown runs on BOTH exit paths (computed before the final exit), recycled-PID guarded — only kills live kubectl port-forwards, keeps the stack + PVCs"

requirements-completed: [SPEC-5, SPEC-6]

# Metrics
duration: 5min
completed: 2026-07-18
---

# Phase 81 Plan 03: k8s Capstone Sweep (phase-81-sweep.ps1) Summary

**Created `scripts/phase-81-sweep.ps1` — the k8s equivalent of the compose capstone sweep: it brings the k8s stack up ONCE (build + up), loops TEST-01..07 as child `phase-80-harness.ps1 -ScenarioId <id> -SkipBringUp` processes with no fail-fast, classifies each via the shared `Resolve-SweepClass` without re-scoring, rolls up to `analyzer-reports/phase-81-summary.json`, and tears the port-forwards down once — exit 0 IFF 7/7 PASS.**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-07-18T09:56Z
- **Completed:** 2026-07-18T10:01Z
- **Tasks:** 2
- **Files created:** 1

## Accomplishments
- Ported the whole compose-sweep skeleton (`$ErrorActionPreference='Stop'`, `Set-StrictMode`, `$repoRoot`/`Push-Location`+`finally{Pop-Location}`, re-prefixed `[phase-81-sweep]` Write-Phase, dot-sourced `lib/exit-code-resolution.ps1`) targeting `phase-80-harness.ps1`.
- Child harness invocation is a separate `pwsh -File phase-80-harness.ps1 -ScenarioId $id -SkipBringUp` process — its `exit N` surfaces as `$LASTEXITCODE` without terminating the loop (no fail-fast: every scenario runs even after a non-PASS).
- Classification + tabulation + roll-up ported byte-identical: `Resolve-SweepClass $code`, the analyzer-report discovery + `[pscustomobject]` row (Verdict/zeroMissing/effectOnce/startedRuns/completeRuns/harnessExit/class), `Format-Table` roll-up, `$passCount==$total ⇒ exit 0`. No inline re-scoring of Missing/Duplicates.
- D-03 STEP 0 one-time build + bring-up (`phase-80-build.ps1` + `phase-80-up.ps1`) added BEFORE the loop — runs once, aborts the sweep (exit 10) on failure.
- D-03 STEP Z one-time port-forward teardown added AFTER the roll-up on both exit paths — recycled-PID guarded (only stops live `kubectl` PIDs from `.k8s-portforward-pids`), keeps the stack + PVCs.
- Roll-up artifact path re-pointed to `analyzer-reports/phase-81-summary.json`.

## Task Commits

Each task was committed atomically:

1. **Task 1: Port phase-68-sweep body → phase-81-sweep.ps1 (loop, classification, roll-up)** - `ef80670` (feat)
2. **Task 2: D-03 one-time bring-up + one-time port-forward teardown** - `25e3fa9` (feat)

## Files Created/Modified
- `scripts/phase-81-sweep.ps1` (created) - k8s capstone sweep driver: STEP 0 one-time build+up, TEST-01..07 child `-SkipBringUp` harness loop with no fail-fast, shared-lib classification with no re-scoring, roll-up to `phase-81-summary.json`, STEP Z one-time port-forward teardown; exit 0 IFF all PASS.

## Decisions Made
- **D-03 seam = Option A (sweep-owned bring-up + harness `-SkipBringUp`).** Keeps `phase-80-up.ps1` unchanged (called once as-is) and gives an explicit "bring up once, run N scenarios" contract. Option B (idempotent self-guard) would require making the monolithic up script's apply/rollout/port-forward steps individually skippable — out of this phase's 3-file scope.
- **Classification is a pure read** of the analyzer's already-computed fields; the shared `lib/exit-code-resolution.ps1` (`Resolve-SweepClass`) is reused verbatim, never re-implemented inline (SPEC-5).
- **Doc-comment reword** — the ported synopsis originally referenced the analog by filename (`phase-68-sweep.ps1`); reworded to "the compose capstone sweep" to satisfy the plan's own acceptance criterion `grep -c 'phase-68' == 0`. (See Deviations.)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Reworded a doc-comment analog reference to hold the zero-phase-68-references acceptance criterion**
- **Found during:** Task 1 (port the sweep body)
- **Issue:** The ported `.DESCRIPTION` explained the D-03 divergence by naming the analog `phase-68-sweep.ps1`. That raised `grep -c 'phase-68' scripts/phase-81-sweep.ps1` to 1, violating the plan's Task 1 acceptance criterion "`grep -c 'phase-68' scripts/phase-81-sweep.ps1` returns 0".
- **Fix:** Reworded "the ONE structural divergence from phase-68-sweep.ps1" to "the ONE structural divergence from the compose capstone sweep". Same meaning; zero `phase-68` references remain.
- **Files modified:** scripts/phase-81-sweep.ps1
- **Verification:** `grep -c 'phase-68'` = 0; PowerShell `Parser::ParseFile` → PARSE_OK.
- **Committed in:** ef80670 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug — acceptance-criterion compliance)
**Impact on plan:** Cosmetic doc-comment wording only; the sweep behavior matches the plan exactly. No scope change.

## Issues Encountered
None. This plan produced the driver script; the live 7/7 behavioral proof (actually running the sweep against a Docker-Desktop k8s stack) is the phase's capstone-run concern, not this authoring plan.

## Verification Results
- `grep -q "phase-80-harness.ps1"` and the invocation carries `-ScenarioId $id -SkipBringUp` → matches (line 90).
- `grep -c 'pwsh -File (Join-Path $PSScriptRoot .phase-80-up.ps1'` = 1 (one-time bring-up, outside the loop; STEP 0 line 87-90 precedes `foreach ($id in $Ids)` line 95).
- `grep -q "phase-80-build.ps1"` + `grep -q ".k8s-portforward-pids"` → both match (STEP 0 build line 88; STEP Z teardown line 159, after the roll-up).
- `grep -c 'TEST-08\|TEST-09\|TEST-10\|FALSIFY'` = 0; `grep -c 'phase-68'` = 0.
- `Resolve-SweepClass` used (line 105); no inline re-computation of Missing/Duplicates (only `$json.Missing`/`$json.Duplicates` reads).
- `phase-81-summary.json` roll-up artifact path present (line 130); `[phase-81-sweep]` Write-Phase tag present.
- PowerShell AST `Parser::ParseFile` → PARSE_OK.
- Post-commit deletion check: no deletions in either commit.

## Threat Model Compliance
- T-81-08 (Spoofing, exit-code misclassification hiding a VERDICT_FAIL): mitigated — classification reuses `lib/exit-code-resolution.ps1` (`Resolve-SweepClass`) verbatim; a FAIL (exit 1) is never remapped to PASS/INCONCLUSIVE and the wrapper never re-scores.
- T-81-09 (Tampering, out-of-scope ids entering the default sweep): mitigated — param default is the fixed 7 capstone ids; `grep -c 'TEST-08\|TEST-09\|TEST-10\|FALSIFY'` = 0.
- T-81-10 (DoS, leaked port-forward processes): mitigated — a single STEP Z teardown stops the recorded kubectl PIDs on both exit paths, recycled-PID guarded (only kills live `kubectl` processes).

## User Setup Required
None - no external service configuration required to author the script. The sweep itself requires a running Docker-Desktop Kubernetes context (namespace `skp`) when executed.

## Next Phase Readiness
- The k8s capstone sweep driver is complete: `pwsh -File scripts/phase-81-sweep.ps1` → build+up once → loop TEST-01..07 as child `-SkipBringUp` runs → roll-up → `phase-81-summary.json` → exit 0 IFF 7/7 PASS.
- Plan 81-04 (the live capstone run / any remaining wave-2 work) can drive this script against a live k8s stack.
- No blockers.

## Self-Check: PASSED

- FOUND: scripts/phase-81-sweep.ps1
- FOUND: commit ef80670
- FOUND: commit 25e3fa9

---
*Phase: 81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o*
*Completed: 2026-07-18*
