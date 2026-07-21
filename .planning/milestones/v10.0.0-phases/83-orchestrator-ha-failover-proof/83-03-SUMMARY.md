---
phase: 83-orchestrator-ha-failover-proof
plan: 03
subsystem: infra
tags: [powershell, kubectl, k8s, leader-election, ha-failover, harness, elasticsearch, exit-codes]

# Dependency graph
requires:
  - phase: 83-02
    provides: "Ha_Failover_Window_Yields_Pass RealStack verdict fact (reads WINDOW_*/KILL_UTC/SCENARIO_ID env seam, writes analyzer-reports/phase-83-ha.json)"
  - phase: 80
    provides: "phase-80-harness.ps1 STEP A0/A/A2/B/B1/C/D/E/Z bring-up primitives + phase-80-build/up/reset scripts"
  - phase: 76
    provides: "scripts/lib/exit-code-resolution.ps1 (Resolve-AnalyzerExitCode / Resolve-SweepClass)"
provides:
  - "scripts/phase-83-ha-failover.ps1 — the operator-plane leader-kill sequencer + HA verdict driver (phase-aligned kill, exit 0/1/2 + infra codes 45/55/60)"
affects: [83-04, ha-failover-proof, orchestrator-ha]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Phase-aligned perturbation: sleep to ~3s before the next :00/:30 cron boundary so the boundary tick deterministically lands in the ~11-17s election gap (resolves the Nyquist gap-coverage problem without changing the locked */30 cron)"
    - "kubectl house style: read Lease spec.holderIdentity, pin $LASTEXITCODE BEFORE .Trim(), namespace-scoped -n skp, force-delete ONLY the read holderIdentity (never a blind replica change)"
    - "Distinct infra-abort exit codes (45 lease-read / 55 force-delete / 60 pre-kill clean-fire) that never masquerade as a verdict"

key-files:
  created:
    - scripts/phase-83-ha-failover.ps1
  modified: []

key-decisions:
  - "STEP Z port-forward teardown moved into the outer finally so it runs even when STEP F/H aborts with an infra code (improvement over phase-80's inline STEP Z which leaks port-forwards on infra abort)"
  - "F.1 pre-kill clean-fire check queries ES _count on exists attributes.StepId since WINDOW_START via Invoke-RestMethod (localhost:9200), tolerating 404 lazy-index as 0 fires"
  - "STEP B0 (processor counter-baseline restart) omitted — no Prometheus/MG-1 conservation scoring in this HA proof"

patterns-established:
  - "Pattern 1: phase-aligned leader kill (($now.Second % 30) offset, force-delete ~3s pre-boundary)"
  - "Pattern 2: report-JSON-authoritative verdict resolution (Resolve-AnalyzerExitCode over the written phase-83-ha.json, env seam cleared in finally)"

requirements-completed: [HA-07]

# Metrics
duration: 3min
completed: 2026-07-18
---

# Phase 83 Plan 03: HA-failover leader-kill sequencer + verdict driver Summary

**`scripts/phase-83-ha-failover.ps1` — reuses the phase-80 STEP A0..E/Z bring-up chain verbatim, then a NEW phase-aligned leader-kill sequencer (read Lease holderIdentity, wait for a clean pre-kill fire, kill ~3s before a :00/:30 boundary, pin KILL_UTC) drives the Plan-02 RealStack verdict and maps phase-83-ha.json through Resolve-AnalyzerExitCode to exit 0/1/2 with distinct infra codes 45/55/60.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-07-18T21:35:17Z
- **Completed:** 2026-07-18T21:38:30Z
- **Tasks:** 2
- **Files modified:** 1 (created)

## Accomplishments
- Built the dedicated `phase-83-ha-failover.ps1` harness (378 lines) — NOT an 8th sweep scenario (D-01)
- Reused the phase-80 STEP A0/A build+up (`-SkipBringUp`), A2 fresh-DB bootstrap, B reset, B1 clean-orchestrator rollout-restart (which also forces a fresh deterministic election), C `~FanOutSeeder` seed, D wfId psql, E POST /start 204 primitives verbatim; STEP B0 omitted
- Added the load-bearing STEP F phase-aligned leader-kill sequencer: F.0 read `spec.holderIdentity` (exit 45), F.1 bounded ES observe for ≥1 clean pre-kill fire (exit 60), F.2 phase-align to ~3s pre-boundary, F.3 `kubectl delete pod --grace-period=0 --force` the exact leader (exit 55) + pin `KILL_UTC`, F.5 hold 120s + pin `WINDOW_END`
- Added STEP H verdict driver: sets the `SCENARIO_ID`/`WINDOW_*_UTC`/`KILL_UTC` env seam (cleared in a `finally`), invokes `*Ha_Failover_Window_Yields_Pass*` via the MTP filter, resolves the authoritative verdict from `phase-83-ha.json` through `Resolve-AnalyzerExitCode` → exit 0/1/2

## Task Commits

Each task was committed atomically:

1. **Task 1: Preamble + exit-code table + reuse-verbatim bring-up chain (STEP A0..E, Z)** - `69bf415` (feat)
2. **Task 2: STEP F phase-aligned leader-kill sequencer + STEP H verdict driver (exit 0/1/2)** - `0c5b73f` (feat)

**Plan metadata:** (final docs commit — see git log)

## Files Created/Modified
- `scripts/phase-83-ha-failover.ps1` - The HA-failover proof harness: phase-80 bring-up chain + phase-aligned leader-kill sequencer + verdict driver

## Decisions Made
- **STEP Z in the outer finally** — placed port-forward teardown in the outer `finally` (per the plan's Task 1 action) so it runs even when STEP F/H aborts with a distinct infra code, improving on phase-80's inline STEP Z (which leaks port-forwards on an infra abort).
- **F.1 ES clean-fire probe** — used `Invoke-RestMethod` against `localhost:9200/logs-generic.otel-default/_count` filtering `exists attributes.StepId` since `WINDOW_START`, wrapped in try/catch to treat 404 lazy-index / transient blips as 0 fires (keep polling to the 90s deadline). This mirrors the send-evidence stream the Plan-02 fact scores, so a clean fire here is the same oracle.
- **B0 omitted** — no Prometheus/MG-1 conservation in this proof, so the processor counter-baseline restart is unnecessary.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None. Both tasks verified clean: PowerShell AST `ParseFile` → `PARSE_OK` after each task; all grep acceptance gates matched (`spec.holderIdentity`, `--grace-period=0`, `--force`, `($now.Second % 30)`, `- 3.0`, `exit 45`/`55`/`60`, `Ha_Failover_Window_Yields_Pass`, `Resolve-AnalyzerExitCode`, `KILL_UTC`; `Remove-Item Env:...KILL_UTC` inside a `finally`; `kubectl scale` and `STEP B0` both absent). Final artifact 378 lines (min 150).

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The harness is AST-parse + grep verified (0 parse errors). The **live end-to-end run against a Docker-Desktop k8s cluster is the Plan 04 checkpoint** — it drives this harness, kills the leader, and expects exit 0 (all five HA-07 claims green). Assumption A1 (post-kill gap ≈ kill+[11,17]s, recovery ≤30s) and A5 (`attributes.role` keyword mapping) are confirmed by that live run.
- No blockers introduced. This wave deliberately does NOT run the live cluster.

## Self-Check: PASSED

- FOUND: `scripts/phase-83-ha-failover.ps1`
- FOUND: `.planning/phases/83-orchestrator-ha-failover-proof/83-03-SUMMARY.md`
- FOUND commit `69bf415` (Task 1)
- FOUND commit `0c5b73f` (Task 2)

---
*Phase: 83-orchestrator-ha-failover-proof*
*Completed: 2026-07-18*
